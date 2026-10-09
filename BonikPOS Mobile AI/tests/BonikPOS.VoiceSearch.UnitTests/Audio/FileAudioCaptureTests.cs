using System.Diagnostics;
using BonikPOS.VoiceSearch.Audio;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Audio;

public sealed class FileAudioCaptureTests
{
    private static async Task<(List<float> Samples, List<AudioFrameInfo> Frames)> ReadAllAsync(IAudioFrameSource source, int delayEveryFrames = 0)
    {
        var ct = TestContext.Current.CancellationToken;
        var samples = new List<float>();
        var infos = new List<AudioFrameInfo>();
        var frame = new float[source.FrameSamples];
        while (true)
        {
            var r = await source.ReadFrameAsync(frame, ct);
            if (!r.HasFrame)
            {
                return (samples, infos);
            }

            samples.AddRange(frame);
            infos.Add(r.Info);
            if (delayEveryFrames > 0 && infos.Count % delayEveryFrames == 0)
            {
                await Task.Delay(1, ct); // a consumer slower than the producer: exercises back-pressure
            }
        }
    }

    [Fact]
    public async Task NonRealTime_DeliversEverySampleInOrder_ThenTheTrailingSilence_WithoutDrops()
    {
        var audio = Signals.Ramp(8480, scale: 1f / 16384); // unique, exactly representable sample values
        await using var capture = new FileAudioCapture(new PcmAudio(audio, 16000, 1), realTime: false, callbackMilliseconds: 10,
            trailingSilenceMilliseconds: 200);

        await capture.StartAsync(new MicrophoneCaptureOptions { FrameMilliseconds = 20, QueueCapacityFrames = 8 }, TestContext.Current.CancellationToken);
        var (samples, frames) = await ReadAllAsync(capture.Frames, delayEveryFrames: 3);

        int total = 8480 + 3200;                       // audio + 200 ms of appended silence
        Assert.Equal(total / 320 * 320, samples.Count); // only whole 20 ms frames are delivered
        Assert.Equal(audio, samples.Take(audio.Length));
        Assert.All(samples.Skip(audio.Length), s => Assert.Equal(0f, s));
        Assert.Equal(Enumerable.Range(0, frames.Count).Select(i => (long)i), frames.Select(f => f.Sequence));
        Assert.DoesNotContain(frames, f => f.Discontinuity);
        Assert.Equal(0, capture.Frames.GetStatistics().FramesDropped);
        await capture.Completion;
    }

    [Fact]
    public async Task NonRealTime_48kStereoFile_IsReducedToChannel0AndResampledTo16k()
    {
        const int rate = 48000;
        var left = Signals.Sine(rate, 500, 0.5, 0.5);
        var interleaved = new float[left.Length * 2];
        for (int i = 0; i < left.Length; i++)
        {
            interleaved[2 * i] = left[i];
            interleaved[2 * i + 1] = 0.9f; // a DC right channel would show up as an offset if channels were mixed
        }

        await using var capture = new FileAudioCapture(new PcmAudio(interleaved, rate, 2), realTime: false, trailingSilenceMilliseconds: 0);
        await capture.StartAsync(new MicrophoneCaptureOptions(), TestContext.Current.CancellationToken);
        var (samples, _) = await ReadAllAsync(capture.Frames);

        Assert.NotNull(capture.ActiveConfiguration);
        Assert.Equal(rate, capture.ActiveConfiguration!.DeviceSampleRate);
        Assert.True(capture.ActiveConfiguration.SoftwareResampling);
        Assert.Equal(8000 / 320 * 320, samples.Count);  // 0.5 s at 16 kHz in whole frames
        Assert.InRange(samples.Skip(100).Average(), -0.01, 0.01);
        Assert.InRange(Signals.Rms(samples.Skip(100).ToArray()), 0.5 / Math.Sqrt(2) * 0.98, 0.5 / Math.Sqrt(2) * 1.02);
    }

    [Fact]
    public async Task StopAsync_MidStream_CancelsThePump_CompletesTheQueue_AndReportsStopped()
    {
        var states = new List<MicrophoneCaptureState>();
        var capture = new FileAudioCapture(new PcmAudio(Signals.Noise(16000 * 60, seed: 1), 16000, 1), realTime: false);
        capture.StateChanged += (_, e) => states.Add(e.State);

        await capture.StartAsync(new MicrophoneCaptureOptions { QueueCapacityFrames = 8 }, TestContext.Current.CancellationToken);
        Assert.Equal(MicrophoneCaptureState.Running, capture.State);

        // Nobody reads: the non-real-time pump must wait for space (back-pressure) instead of dropping frames.
        var first = new float[320];
        Assert.True((await capture.Frames.ReadFrameAsync(first, TestContext.Current.CancellationToken)).HasFrame);
        await capture.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MicrophoneCaptureState.Stopped, capture.State);
        Assert.Equal([MicrophoneCaptureState.Running, MicrophoneCaptureState.Stopping, MicrophoneCaptureState.Stopped], states);
        var (rest, _) = await ReadAllAsync(capture.Frames); // queued frames drain, then completion is reported
        var stats = capture.Frames.GetStatistics();
        Assert.Equal(0, stats.FramesDropped);
        Assert.True(stats.FramesWritten < 60 * 50, "the pump should have stopped long before the end of the file");
        Assert.Equal((stats.FramesWritten - 1) * 320, rest.Count);
        await capture.StopAsync(TestContext.Current.CancellationToken); // idempotent
    }

    [Fact]
    public async Task StartAsync_WhileRunning_Throws_AndARestartReplaysFromTheBeginning()
    {
        var audio = Signals.Ramp(3200, scale: 1f / 8192);
        await using var capture = new FileAudioCapture(new PcmAudio(audio, 16000, 1), realTime: false, trailingSilenceMilliseconds: 0);
        var options = new MicrophoneCaptureOptions();
        var ct = TestContext.Current.CancellationToken;

        await capture.StartAsync(options, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StartAsync(options, ct));
        var (firstRun, _) = await ReadAllAsync(capture.Frames);
        await capture.StopAsync(ct);

        await capture.StartAsync(options, ct);
        var (secondRun, _) = await ReadAllAsync(capture.Frames);
        await capture.StopAsync(ct);

        Assert.Equal(audio, firstRun);
        Assert.Equal(audio, secondRun);
    }

    [Fact]
    public async Task RealTime_PacesCallbacksAtTheAudioRate()
    {
        var audio = Signals.Noise(1600, seed: 2); // 100 ms
        await using var capture = new FileAudioCapture(new PcmAudio(audio, 16000, 1), realTime: true, callbackMilliseconds: 10,
            trailingSilenceMilliseconds: 0);
        var sw = Stopwatch.StartNew();

        await capture.StartAsync(new MicrophoneCaptureOptions(), TestContext.Current.CancellationToken);
        var (samples, _) = await ReadAllAsync(capture.Frames);

        // The last 10 ms callback is due 90 ms after the start; a non-paced pump would finish in a few milliseconds.
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(80), $"finished after {sw.Elapsed.TotalMilliseconds} ms");
        Assert.Equal(audio, samples);
    }
}

public sealed class WavFileTests
{
    [Fact]
    public void Write16Bit_ThenRead_RoundTripsWithinOneQuantisationStep()
    {
        var signal = Signals.Sine(16000, 300, 0.25, 0.8).Concat([1.5f, -1.5f]).ToArray(); // out-of-range values are clamped
        using var stream = new MemoryStream();
        WavFile.Write(stream, signal, 16000);
        stream.Position = 0;

        var read = WavFile.Read(stream);

        Assert.Equal(16000, read.SampleRate);
        Assert.Equal(1, read.Channels);
        Assert.Equal(signal.Length, read.Samples.Length);
        for (int i = 0; i < signal.Length; i++)
        {
            Assert.InRange(read.Samples[i] - Math.Clamp(signal[i], -1f, 1f), -1.5 / 32768, 1.5 / 32768);
        }
    }

    [Fact]
    public void Read_SkipsUnknownChunks_AndDecodesFloat32()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            float[] data = [0.25f, -0.5f, 1f];
            w.Write("RIFF"u8);
            w.Write(0);
            w.Write("WAVE"u8);
            w.Write("LIST"u8);
            w.Write(3);              // odd-sized chunk: followed by a pad byte
            w.Write(new byte[] { 1, 2, 3, 0 });
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)3);       // IEEE float
            w.Write((short)2);
            w.Write(8000);
            w.Write(8000 * 8);
            w.Write((short)8);
            w.Write((short)32);
            w.Write("data"u8);
            w.Write(data.Length * 4);
            foreach (var f in data)
            {
                w.Write(f);
            }
        }

        ms.Position = 0;
        var audio = WavFile.Read(ms);

        Assert.Equal(8000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal([0.25f, -0.5f, 1f], audio.Samples);
    }

    [Fact]
    public void Read_RejectsNonWaveData()
    {
        Assert.Throws<InvalidDataException>(() => WavFile.Read(new MemoryStream("RIFX0000WAVE"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => WavFile.Read(new MemoryStream("RIFF\0\0\0\0AVI "u8.ToArray())));
    }

    [Fact]
    public void PcmAudio_ToMono_TakesChannel0()
    {
        var audio = new PcmAudio([1f, 9f, 2f, 9f, 3f, 9f], 16000, 2);
        Assert.Equal(3, audio.Frames);
        Assert.Equal([1f, 2f, 3f], audio.ToMono());
        Assert.Equal(TimeSpan.FromSeconds(3.0 / 16000), audio.Duration);
    }
}
