using BonikPOS.VoiceSearch.Audio;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Audio;

public sealed class CaptureProcessorTests
{
    private const int Frame = 320; // 20 ms at 16 kHz

    /// <summary>Drains every queued frame (the queue is large enough that nothing is dropped).</summary>
    private static List<float> Drain(AudioFrameQueue queue)
    {
        Assert.Equal(0, queue.GetStatistics().FramesDropped);
        queue.Complete();
        var all = new List<float>();
        var dest = new float[queue.FrameSamples];
        while (true)
        {
            var r = queue.ReadFrameAsync(dest, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (!r.HasFrame)
            {
                return all;
            }

            all.AddRange(dest);
        }
    }

    private static AudioFrameQueue BigQueue(int frameSamples = Frame) => new(AudioStreamFormat.Speech16kMono, frameSamples, 4000);

    [Fact]
    public void Constructor_RequiresA16kMonoQueue()
    {
        var stereo = new AudioFrameQueue(new AudioStreamFormat(16000, 2), Frame, 4);
        var wrongRate = new AudioFrameQueue(new AudioStreamFormat(48000, 1), Frame, 4);
        Assert.Throws<ArgumentException>(() => new CaptureProcessor(16000, stereo, 1024));
        Assert.Throws<ArgumentException>(() => new CaptureProcessor(16000, wrongRate, 1024));
    }

    [Fact]
    public void At16k_FramesHaveExactlyTheConfiguredSize_AndARemainderWaitsForMoreAudio()
    {
        var queue = BigQueue();
        var processor = new CaptureProcessor(16000, queue, 4096);
        Assert.False(processor.IsResampling);
        var signal = Signals.Ramp(1000);

        processor.Process(signal, 0);
        Assert.Equal(3, processor.FramesProduced); // 960 samples framed, 40 pending
        Assert.Equal(3, queue.GetStatistics().FramesWritten);

        processor.Process(Signals.Ramp(280, scale: 0), 0); // completes the 4th frame exactly
        Assert.Equal(4, processor.FramesProduced);

        var output = Drain(queue);
        Assert.Equal(4 * Frame, output.Count);
        Assert.Equal(signal, output.Take(1000));          // bit-exact pass-through without resampling
        Assert.All(output.Skip(1000), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void ShortSamples_AreConvertedToFloatByDividingBy32768()
    {
        var queue = BigQueue(frameSamples: 6);
        var processor = new CaptureProcessor(16000, queue, 256);
        short[] pcm = [short.MinValue, -16384, -1, 0, 16384, short.MaxValue];

        processor.Process(pcm, 0);

        var output = Drain(queue);
        Assert.Equal([-1f, -0.5f, -1f / 32768, 0f, 0.5f, 32767f / 32768], output);
    }

    [Fact]
    public void ShortSamples_InBlocksLargerThanTheCallbackBuffer_AreAllConverted()
    {
        var queue = BigQueue();
        var processor = new CaptureProcessor(16000, queue, maxCallbackSamples: 256); // internal block = 256
        var pcm = new short[Frame * 5];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(i * 7 - 5000);
        }

        processor.Process(pcm, 0);

        var output = Drain(queue);
        Assert.Equal(pcm.Length, output.Count);
        for (int i = 0; i < pcm.Length; i++)
        {
            Assert.Equal(pcm[i] / 32768f, output[i]);
        }
    }

    /// <remarks>
    /// The documented behaviour (CaptureProcessor.ProcessInterleaved XML doc and PcmAudio.ToMono) is "channel 0 is
    /// used" - it selects the first channel rather than averaging the channels.
    /// </remarks>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void InterleavedMultiChannel_IsReducedToChannel0(int channels)
    {
        var queue = BigQueue();
        var processor = new CaptureProcessor(16000, queue, 300);
        int frames = Frame * 3;
        var interleaved = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            interleaved[i * channels] = i / 4096f;          // channel 0: ramp
            for (int c = 1; c < channels; c++)
            {
                interleaved[i * channels + c] = -0.9f + c;  // other channels: constants that must not appear
            }
        }

        processor.ProcessInterleaved(interleaved, channels, 0);

        var output = Drain(queue);
        Assert.Equal(frames, output.Count);
        for (int i = 0; i < frames; i++)
        {
            Assert.Equal(i / 4096f, output[i]);
        }
    }

    [Fact]
    public void InterleavedMono_IsPassedThrough()
    {
        var queue = BigQueue();
        var processor = new CaptureProcessor(16000, queue, 300);
        var mono = Signals.Ramp(Frame * 2);

        processor.ProcessInterleaved(mono, 1, 0);

        Assert.Equal(mono, Drain(queue));
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Resampling_ChunkedCallbacksProduceExactlyTheOneShotOutput(int deviceRate)
    {
        // 1.3 s of a tone plus noise: any discontinuity at a block boundary would change samples.
        var tone = Signals.Sine(deviceRate, 440, 1.3, 0.4);
        var noise = Signals.Noise(tone.Length, seed: 99, amplitude: 0.1);
        var signal = tone.Zip(noise, (a, b) => a + b).ToArray();

        var oneShotQueue = BigQueue();
        var oneShot = new CaptureProcessor(deviceRate, oneShotQueue, maxCallbackSamples: signal.Length);
        Assert.True(oneShot.IsResampling);
        oneShot.Process(signal, 0);

        var chunkedQueue = BigQueue();
        var chunked = new CaptureProcessor(deviceRate, chunkedQueue, maxCallbackSamples: 1024);
        int offset = 0;
        foreach (int n in Signals.RandomBlocks(signal.Length, maxBlock: 3000, seed: deviceRate))
        {
            chunked.Process(signal.AsSpan(offset, n), 0); // blocks above 1024 are also split internally
            offset += n;
        }

        var a = Drain(oneShotQueue);
        var b = Drain(chunkedQueue);
        Assert.Equal(oneShot.FramesProduced, chunked.FramesProduced);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a, b); // bit-identical
        Assert.InRange(a.Count, signal.Length * 16000L / deviceRate - Frame, signal.Length * 16000L / deviceRate);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Resampling_ShortSampleCallbacks_MatchFloatCallbacks(int deviceRate)
    {
        var pcm = new short[deviceRate / 2];
        var rng = new Random(5);
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)rng.Next(-20000, 20000);
        }

        var floats = pcm.Select(s => s / 32768f).ToArray();
        var q1 = BigQueue();
        new CaptureProcessor(deviceRate, q1, floats.Length).Process(floats, 0);

        var q2 = BigQueue();
        var chunked = new CaptureProcessor(deviceRate, q2, 512);
        int offset = 0;
        foreach (int n in Signals.RandomBlocks(pcm.Length, 900, seed: 17))
        {
            chunked.Process(pcm.AsSpan(offset, n), 0);
            offset += n;
        }

        Assert.Equal(Drain(q1), Drain(q2));
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Resampling_KeepsAnInBandToneAtItsFrequencyAndLevel(int deviceRate)
    {
        var queue = BigQueue();
        var processor = new CaptureProcessor(deviceRate, queue, 2048);
        processor.Process(Signals.Sine(deviceRate, 1000, 1.0, 0.5), 0);
        var output = Drain(queue).ToArray();

        Assert.Equal(16000, output.Length); // 1 s at the device rate -> 50 frames of 20 ms at 16 kHz

        // Skip the filter start-up, then compare with the ideal 1 kHz tone at 16 kHz by least-squares fit.
        var steady = output.AsSpan(200, 15000);
        double sumSin = 0, sumCos = 0;
        for (int i = 0; i < steady.Length; i++)
        {
            double t = 2 * Math.PI * 1000 * (i + 200) / 16000.0;
            sumSin += steady[i] * Math.Sin(t);
            sumCos += steady[i] * Math.Cos(t);
        }

        double amplitude = 2 * Math.Sqrt(sumSin * sumSin + sumCos * sumCos) / steady.Length;
        Assert.InRange(amplitude, 0.495, 0.505); // pass-band gain within 1 %

        // The tone must be the only content: residual after removing the fitted sinusoid is tiny.
        double a = 2 * sumSin / steady.Length, b = 2 * sumCos / steady.Length, residual = 0;
        for (int i = 0; i < steady.Length; i++)
        {
            double t = 2 * Math.PI * 1000 * (i + 200) / 16000.0;
            double e = steady[i] - (a * Math.Sin(t) + b * Math.Cos(t));
            residual += e * e;
        }

        Assert.True(Math.Sqrt(residual / steady.Length) < 0.005, $"residual RMS {Math.Sqrt(residual / steady.Length)}");
    }

    [Fact]
    public void Resampling48k_AttenuatesContentAboveTheNewNyquist()
    {
        var queue = BigQueue();
        var processor = new CaptureProcessor(48000, queue, 4096);
        processor.Process(Signals.Sine(48000, 15000, 1.0, 0.5), 0); // would alias to 1 kHz without filtering

        var output = Drain(queue).ToArray();
        double rms = Signals.Rms(output.AsSpan(200));
        Assert.True(rms < 0.5 / Math.Sqrt(2) * 0.01, $"15 kHz leaked with RMS {rms} (needs > 40 dB attenuation)");
    }

    [Fact]
    public void Reset_DropsThePartialFrameAndTheResamplerHistory()
    {
        var fresh = Signals.Noise(9600, seed: 3);

        var reference = BigQueue();
        new CaptureProcessor(48000, reference, 2048).Process(fresh, 0);

        var queue = BigQueue();
        var processor = new CaptureProcessor(48000, queue, 2048);
        processor.Process(Signals.Noise(1000, seed: 4), 0); // leaves history and a partial frame behind
        var before = queue.GetStatistics().FramesWritten;
        processor.Reset();
        processor.Process(fresh, 0);

        var all = Drain(queue);
        var afterReset = all.Skip((int)before * Frame).ToList();
        Assert.Equal(Drain(reference), afterReset);
    }

    [Fact]
    public void Process_WithAFullQueue_DropsOldestInsteadOfBlocking()
    {
        var queue = new AudioFrameQueue(AudioStreamFormat.Speech16kMono, Frame, 2);
        var processor = new CaptureProcessor(16000, queue, 4096);

        processor.Process(Signals.Ramp(Frame * 10), 0); // must return without a consumer

        var stats = queue.GetStatistics();
        Assert.Equal(10, processor.FramesProduced);
        Assert.Equal(10, stats.FramesWritten);
        Assert.Equal(8, stats.FramesDropped);
    }
}

public sealed class PolyphaseResamplerTests
{
    [Theory]
    [InlineData(48000, 16000)]
    [InlineData(44100, 16000)]
    [InlineData(8000, 16000)]
    public void ConstantInput_HasUnityGainAfterTheFilterDelay(int inRate, int outRate)
    {
        var r = new PolyphaseResampler(inRate, outRate);
        var input = new float[inRate / 2];
        Array.Fill(input, 0.5f);
        var output = new float[r.MaxOutputFor(input.Length)];

        int n = r.Process(input, output);

        int settle = (int)Math.Ceiling(r.LatencyOutputSamples * 2) + 2;
        Assert.InRange(n, input.Length * outRate / inRate, input.Length * outRate / inRate + 1);
        for (int i = settle; i < n; i++)
        {
            Assert.InRange(output[i], 0.4985f, 0.5015f);
        }
    }

    [Fact]
    public void Process_RejectsAnOutputBufferSmallerThanMaxOutputFor()
    {
        var r = new PolyphaseResampler(48000, 16000);
        var input = new float[300];
        Assert.Throws<ArgumentException>(() => r.Process(input, new float[r.MaxOutputFor(300) - 1]));
    }

    [Fact]
    public void MaxOutputFor_IsAnUpperBoundForEveryBlockSize()
    {
        var r = new PolyphaseResampler(44100, 16000);
        var output = new float[r.MaxOutputFor(5000)];
        var rng = new Random(8);
        long totalIn = 0, totalOut = 0;
        for (int i = 0; i < 500; i++)
        {
            int n = rng.Next(0, 5000);
            int produced = r.Process(new float[n], output);
            Assert.True(produced <= r.MaxOutputFor(n));
            totalIn += n;
            totalOut += produced;
        }

        // Over many blocks the rate ratio is exact (to within one sample).
        Assert.InRange(totalOut, totalIn * 160 / 441, totalIn * 160 / 441 + 1);
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolyphaseResampler(0, 16000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolyphaseResampler(48000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolyphaseResampler(48000, 16000, tapsPerPhase: 3));
    }
}
