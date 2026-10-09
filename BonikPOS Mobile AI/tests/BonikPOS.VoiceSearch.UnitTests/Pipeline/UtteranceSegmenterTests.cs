using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Pipeline;

/// <summary>
/// The VAD is replaced by a scripted probability per 10 ms chunk (160 samples). Every sample of chunk k carries the
/// value k + 1, so the tests can tell exactly which chunks ended up in an utterance.
/// </summary>
public sealed class UtteranceSegmenterTests
{
    private const int Chunk = 160;

    private static readonly UtteranceSegmenterOptions Options = new()
    {
        StartThreshold = 0.5,
        EndThreshold = 0.35,
        MinSpeechMilliseconds = 100,
        EndSilenceMilliseconds = 50,  // 5 chunks
        PreRollMilliseconds = 50,     // 5 chunks (more than the 2 start chunks)
        MaxUtteranceMilliseconds = 1000,
        StartChunks = 2,
    };

    private sealed record Emitted(Utterance Utterance, int AtChunk);

    private static float[] Signal(int chunks)
    {
        var s = new float[chunks * Chunk];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = i / Chunk + 1;
        }

        return s;
    }

    /// <summary>Chunk ids contained in a buffer (asserting it consists of whole, unmodified chunks).</summary>
    private static int[] ChunkIds(float[] audio)
    {
        Assert.Equal(0, audio.Length % Chunk);
        var ids = new int[audio.Length / Chunk];
        for (int c = 0; c < ids.Length; c++)
        {
            float v = audio[c * Chunk];
            for (int i = 0; i < Chunk; i++)
            {
                Assert.Equal(v, audio[c * Chunk + i]);
            }

            ids[c] = (int)v - 1;
        }

        return ids;
    }

    private static int[] Range(int first, int lastInclusive) => [.. Enumerable.Range(first, lastInclusive - first + 1)];

    private static List<Emitted> Run(UtteranceSegmenter segmenter, float[] script, int pushSamples = Chunk, bool flush = false)
    {
        var signal = Signal(script.Length);
        var emitted = new List<Emitted>();
        for (int offset = 0; offset < signal.Length; offset += pushSamples)
        {
            int n = Math.Min(pushSamples, signal.Length - offset);
            var u = segmenter.Push(signal.AsSpan(offset, n), captureTimestamp: offset);
            if (u is not null)
            {
                emitted.Add(new Emitted(u, (offset + n - 1) / Chunk));
            }
        }

        if (flush && segmenter.Flush() is { } last)
        {
            emitted.Add(new Emitted(last, script.Length));
        }

        return emitted;
    }

    [Fact]
    public void SpeechBetweenSilences_IsCutWithPreRollAndHangover()
    {
        var script = ScriptedVad.Script((0.1f, 10), (0.9f, 20), (0.1f, 10));
        var segmenter = new UtteranceSegmenter(new ScriptedVad(script), Options);

        var emitted = Run(segmenter, script);

        var e = Assert.Single(emitted);
        Assert.Equal(34, e.AtChunk);                                // 5th silent chunk after the speech
        Assert.Equal(Range(7, 34), ChunkIds(e.Utterance.Audio));      // 3 chunks of pre-roll + 2 start chunks + speech + hang-over
        Assert.Equal(Range(10, 29), ChunkIds(e.Utterance.SpeechOnly)); // only VAD-positive chunks
        Assert.Equal(200, e.Utterance.SpeechMilliseconds);
        Assert.False(e.Utterance.Truncated);
        Assert.Equal(16000, e.Utterance.SampleRate);
        Assert.False(segmenter.InSpeech);
    }

    [Fact]
    public void InSpeech_AndLastProbability_FollowTheVad()
    {
        var script = ScriptedVad.Script((0.1f, 3), (0.9f, 2), (0.6f, 1));
        var segmenter = new UtteranceSegmenter(new ScriptedVad(script), Options);
        var signal = Signal(script.Length);
        var states = new List<bool>();
        for (int c = 0; c < script.Length; c++)
        {
            segmenter.Push(signal.AsSpan(c * Chunk, Chunk), 0);
            states.Add(segmenter.InSpeech);
            Assert.Equal(script[c], segmenter.LastProbability);
        }

        Assert.Equal([false, false, false, false, true, true], states);
        Assert.Equal(10, segmenter.ChunkMilliseconds);
    }

    [Fact]
    public void SpeechShorterThanTheMinimum_IsDiscarded_AndTheNextUtteranceStillWorks()
    {
        var script = ScriptedVad.Script((0.1f, 10), (0.9f, 3), (0.1f, 10), (0.9f, 15), (0.1f, 6));
        var segmenter = new UtteranceSegmenter(new ScriptedVad(script), Options);

        var emitted = Run(segmenter, script);

        var e = Assert.Single(emitted);                               // 30 ms of speech < 100 ms minimum
        Assert.Equal(Range(20, 42), ChunkIds(e.Utterance.Audio));
        Assert.Equal(Range(23, 37), ChunkIds(e.Utterance.SpeechOnly));
        Assert.Equal(150, e.Utterance.SpeechMilliseconds);
    }

    [Fact]
    public void IsolatedSpeechChunks_DoNotOpenAnUtterance()
    {
        var script = Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? 0.95f : 0.1f).ToArray();
        var segmenter = new UtteranceSegmenter(new ScriptedVad(script), Options);

        Assert.Empty(Run(segmenter, script, flush: true));
        Assert.False(segmenter.InSpeech);
    }

    [Fact]
    public void Hysteresis_StartNeedsTheStartThreshold_ContinuationOnlyTheEndThreshold()
    {
        // 0.45 never starts speech; once started, 0.4 keeps it going; 0.3 counts as silence.
        var script = ScriptedVad.Script((0.45f, 10), (0.9f, 2), (0.4f, 10), (0.3f, 5));
        var segmenter = new UtteranceSegmenter(new ScriptedVad(script), Options);

        var e = Assert.Single(Run(segmenter, script));

        Assert.Equal(Range(7, 26), ChunkIds(e.Utterance.Audio));
        Assert.Equal(Range(10, 21), ChunkIds(e.Utterance.SpeechOnly));
        Assert.Equal(120, e.Utterance.SpeechMilliseconds);
    }

    [Fact]
    public void PausesShorterThanTheEndSilence_DoNotSplitTheUtterance()
    {
        var script = ScriptedVad.Script((0.1f, 5), (0.9f, 10), (0.1f, 4), (0.9f, 10), (0.1f, 5));
        var segmenter = new UtteranceSegmenter(new ScriptedVad(script), Options);

        var e = Assert.Single(Run(segmenter, script));

        Assert.Equal(Range(2, 33), ChunkIds(e.Utterance.Audio));
        Assert.Equal([.. Range(5, 14), .. Range(19, 28)], ChunkIds(e.Utterance.SpeechOnly));
        Assert.Equal(200, e.Utterance.SpeechMilliseconds);
    }

    [Fact]
    public void LongSpeech_IsTruncatedAtTheMaximumLength_AndContinuesWithoutOverlap()
    {
        var options = Options with { MaxUtteranceMilliseconds = 200 }; // 20 chunks + 5 chunks of pre-roll capacity
        var script = ScriptedVad.Script((0.1f, 10), (0.9f, 60));
        var segmenter = new UtteranceSegmenter(new ScriptedVad(script), options);

        var emitted = Run(segmenter, script, flush: true);

        Assert.Equal(3, emitted.Count);
        Assert.True(emitted[0].Utterance.Truncated);
        Assert.True(emitted[1].Utterance.Truncated);
        Assert.False(emitted[2].Utterance.Truncated);                // the remainder is flushed at the end of the stream
        Assert.Equal(Range(7, 31), ChunkIds(emitted[0].Utterance.Audio));
        Assert.Equal(Range(32, 56), ChunkIds(emitted[1].Utterance.Audio));
        Assert.Equal(Range(57, 69), ChunkIds(emitted[2].Utterance.Audio));
        Assert.Equal(220, emitted[0].Utterance.SpeechMilliseconds);
        foreach (var e in emitted)
        {
            Assert.True(e.Utterance.Audio.Length <= 16000 * (200 + 50) / 1000);
        }
    }

    [Fact]
    public void Flush_ReturnsTheUtteranceInProgress_OnlyIfItHasEnoughSpeech()
    {
        var script = ScriptedVad.Script((0.1f, 5), (0.9f, 15));
        var segmenter = new UtteranceSegmenter(new ScriptedVad(script), Options);
        Assert.Empty(Run(segmenter, script));
        Assert.True(segmenter.InSpeech);

        var u = segmenter.Flush();

        Assert.NotNull(u);
        Assert.Equal(Range(2, 19), ChunkIds(u!.Audio));
        Assert.False(u.Truncated);
        Assert.False(segmenter.InSpeech);
        Assert.Null(segmenter.Flush());

        var shortScript = ScriptedVad.Script((0.1f, 5), (0.9f, 3));
        var shortSegmenter = new UtteranceSegmenter(new ScriptedVad(shortScript), Options);
        Run(shortSegmenter, shortScript);
        Assert.True(shortSegmenter.InSpeech);
        Assert.Null(shortSegmenter.Flush());                          // 30 ms < minimum speech
    }

    [Fact]
    public void Reset_DiscardsThePendingUtterance_PartialChunkAndVadState()
    {
        var vad = new ScriptedVad(ScriptedVad.Script((0.9f, 12), (0.1f, 20)));
        var segmenter = new UtteranceSegmenter(vad, Options);
        var signal = Signal(12);
        foreach (var offset in Enumerable.Range(0, 12).Select(c => c * Chunk))
        {
            Assert.Null(segmenter.Push(signal.AsSpan(offset, Chunk), 0));
        }

        Assert.True(segmenter.InSpeech);
        segmenter.Push(new float[100], 0); // partial chunk

        segmenter.Reset();

        Assert.Equal(1, vad.ResetCount);
        Assert.False(segmenter.InSpeech);
        Assert.Equal(0, segmenter.LastProbability);
        var fresh = Enumerable.Repeat(-0.25f, Chunk).ToArray();
        Assert.Null(segmenter.Push(fresh, 0));
        Assert.Equal(fresh, vad.Chunks[^1]); // the 100 samples from before the reset did not leak into this chunk
        for (int i = 0; i < 10; i++)
        {
            Assert.Null(segmenter.Push(fresh, 0)); // silence after the reset never completes the discarded utterance
        }

        Assert.Null(segmenter.Flush());
    }

    [Theory]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(320)]
    [InlineData(512)]
    public void PushGranularity_DoesNotChangeTheResult(int pushSamples)
    {
        var script = ScriptedVad.Script((0.1f, 8), (0.9f, 25), (0.2f, 12), (0.8f, 14), (0.1f, 9));
        var expected = Run(new UtteranceSegmenter(new ScriptedVad(script), Options), script);
        var actual = Run(new UtteranceSegmenter(new ScriptedVad(script), Options), script, pushSamples);

        Assert.Equal(2, expected.Count);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Utterance.Audio, actual[i].Utterance.Audio);
            Assert.Equal(expected[i].Utterance.SpeechOnly, actual[i].Utterance.SpeechOnly);
            Assert.Equal(expected[i].Utterance.SpeechMilliseconds, actual[i].Utterance.SpeechMilliseconds);
        }
    }

    [Fact]
    public void Constructor_RejectsAnEndThresholdAboveTheStartThreshold()
    {
        Assert.Throws<ArgumentException>(() =>
            new UtteranceSegmenter(new ScriptedVad([]), Options with { StartThreshold = 0.4, EndThreshold = 0.6 }));
    }
}
