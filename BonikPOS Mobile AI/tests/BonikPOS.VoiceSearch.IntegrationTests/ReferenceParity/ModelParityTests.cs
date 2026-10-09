using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Speakers;
using BonikPOS.VoiceSearch.IntegrationTests.TestSupport;

namespace BonikPOS.VoiceSearch.IntegrationTests.ReferenceParity;

/// <summary>
/// The bundled models run through the C# adapters against references produced with the upstream Python
/// implementations (silero-vad OnnxWrapper, Resemblyzer VoiceEncoder in PyTorch). The audio is synthetic TTS: this
/// verifies tensor plumbing and feature parity, not speech-detection or speaker-verification accuracy.
/// </summary>
public sealed class ModelParityTests(BundledModelsFixture models, ITestOutputHelper output) : IClassFixture<BundledModelsFixture>
{
    private static JsonElement Reference(string section)
    {
        using var doc = SolutionPaths.ReadJson(SolutionPaths.ReferenceFile("reference.json"));
        return doc.RootElement.GetProperty(section).Clone();
    }

    [Theory]
    [InlineData("synthetic-en-napa.wav")]
    [InlineData("noise-2s.wav")]
    [InlineData("silence-1s.wav")]
    public void SileroVad_StreamingProbabilities_MatchTheOfficialWrapper(string clip)
    {
        var reference = Reference("silero_vad_probabilities");
        Assert.Equal(512, reference.GetProperty("chunk_samples").GetInt32());
        Assert.Equal(64, reference.GetProperty("context_samples").GetInt32());
        var expected = reference.GetProperty("per_file").GetProperty(clip).EnumerateArray().Select(e => e.GetDouble()).ToList();
        var wav = SolutionPaths.ReadClip(clip);
        using var vad = models.Runtime.CreateVoiceActivityDetector();
        Assert.Equal(512, vad.ChunkSamples);

        var actual = new List<double>();
        for (int i = 0; i + 512 <= wav.Length; i += 512)
        {
            actual.Add(vad.Process(wav.AsSpan(i, 512)));
        }

        double maxAbs = Errors.MaxAbs(actual, expected);
        output.WriteLine($"Silero VAD on {clip}: {actual.Count} chunks, max |C# - Python| = {maxAbs:E3}");
        // Same weights (the bundled 16 kHz graph gives outputs identical to silero_vad.onnx); measured bit-identical here,
        // 1e-5 leaves room for CPU-specific float32 kernels.
        Assert.True(maxAbs <= 1e-5, $"max abs error {maxAbs:E3}");

        // State is carried from chunk to chunk; Reset() starts a stream again from scratch.
        vad.Reset();
        Assert.Equal(expected[0], vad.Process(wav.AsSpan(0, 512)), 1e-5);
    }

    [Fact]
    public void SileroVad_SeparatesSyntheticSpeechFromSilence()
    {
        using var vad = models.Runtime.CreateVoiceActivityDetector();
        var silence = SolutionPaths.ReadClip("silence-1s.wav");
        var speech = SolutionPaths.ReadClip("synthetic-en-napa.wav");
        double maxSilence = 0, maxSpeech = 0;
        for (int i = 0; i + 512 <= silence.Length; i += 512)
        {
            maxSilence = Math.Max(maxSilence, vad.Process(silence.AsSpan(i, 512)));
        }

        vad.Reset();
        for (int i = 0; i + 512 <= speech.Length; i += 512)
        {
            maxSpeech = Math.Max(maxSpeech, vad.Process(speech.AsSpan(i, 512)));
        }

        output.WriteLine($"max probability: silence {maxSilence:F4}, speech {maxSpeech:F4}");
        Assert.True(maxSilence < 0.1);
        Assert.True(maxSpeech > 0.9);
        Assert.Throws<ArgumentException>(() => vad.Process(new float[320]));
    }

    [Fact]
    public void Ge2eUtteranceEmbeddings_MatchResemblyzer()
    {
        var reference = Reference("ge2e_utterance_embeddings");
        var encoder = Assert.IsType<Ge2eSpeakerEncoder>(models.Runtime.SpeakerEncoder);
        var expected = reference.GetProperty("embeddings").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetSingle()).ToArray());
        Assert.Equal(7, expected.Count);

        var actual = new Dictionary<string, float[]>();
        double worstCosine = 1, worstElement = 0;
        foreach (var (clip, reference256) in expected)
        {
            // Resemblyzer's reference embedded the raw clip. The C# encoder raises quiet input to -30 dBFS (as
            // Resemblyzer's preprocess_wav does); every clip here is louder (about -18 to -22 dBFS), so it is unchanged.
            var embedding = encoder.EmbedUtterance(SolutionPaths.ReadClip(clip), TestContext.Current.CancellationToken);
            Assert.Equal(256, embedding.Dimension);
            actual[clip] = embedding.Vector;
            double cosine = Errors.Cosine(embedding.Vector, reference256);
            double element = Errors.MaxAbs(embedding.Vector, reference256);
            output.WriteLine($"{clip,-30} cosine(C#, PyTorch) = {cosine:F7}  max |element diff| = {element:E3}");
            worstCosine = Math.Min(worstCosine, cosine);
            worstElement = Math.Max(worstElement, element);
        }

        // ONNX export of the same weights + an independent mel front-end: measured float32-level agreement (elements
        // within 3.1e-7, cosine 1.0000000); the bounds leave ~30x for CPU-specific LSTM kernels.
        Assert.True(worstCosine >= 0.99999, $"worst cosine {worstCosine:F7}");
        Assert.True(worstElement <= 1e-5, $"worst element error {worstElement:E3}");

        // The pairwise similarity structure is reproduced as well.
        var cosines = reference.GetProperty("cosine");
        double worstPair = 0;
        foreach (var a in expected.Keys)
        {
            foreach (var b in expected.Keys)
            {
                double expectedCosine = cosines.GetProperty(a).GetProperty(b).GetDouble();
                worstPair = Math.Max(worstPair, Math.Abs(Errors.Cosine(actual[a], actual[b]) - expectedCosine));
            }
        }

        output.WriteLine($"max |pairwise cosine difference| = {worstPair:E3}");
        Assert.True(worstPair <= 1e-5, $"pairwise cosine error {worstPair:E3}"); // measured 1.6e-7
    }

    [Fact]
    public void Ge2ePartialSlices_FollowResemblyzer()
    {
        var encoder = Assert.IsType<Ge2eSpeakerEncoder>(models.Runtime.SpeakerEncoder);

        // compute_partial_slices(n_samples, rate=1.3, min_coverage=0.75): partials of 160 frames, one every
        // round(16000 / 1.3 / 160) = 77 frames; the last one is dropped when it covers < 75 % real audio (unless alone).
        Assert.Equal([0], encoder.PartialSlices(16000, 1.3, 0.75));        // a single short partial is kept
        Assert.Equal([0], encoder.PartialSlices(28000, 1.3, 0.75));        // [0, 77]: second covers 61 % -> dropped
        Assert.Equal([0, 77], encoder.PartialSlices(35000, 1.3, 0.75));    // second covers 89 %
        Assert.Equal([0, 77, 154], encoder.PartialSlices(45280, 1.3, 0.75)); // synthetic-voiceA-1.wav
        Assert.Throws<ArgumentException>(() => encoder.EmbedUtterance(new float[1000], TestContext.Current.CancellationToken));
    }
}
