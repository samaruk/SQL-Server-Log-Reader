using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Pipeline;

namespace BonikPOS.VoiceSearch.Core.Enrollment;

public sealed record MicrophoneTestResult(RecordingQualityReport Quality, MicrophoneCaptureInfo? Capture, AudioQueueStatistics Queue);

/// <summary>Records a few seconds (nothing is stored), measures noise floor, speech level, SNR and clipping.</summary>
public sealed class MicrophoneTester(IMicrophoneCapture capture, IVoiceModelRuntime runtime)
{
    public async Task<MicrophoneTestResult> RunAsync(TimeSpan duration, IProgress<LevelReading>? level, CancellationToken cancellationToken)
    {
        int total = (int)(duration.TotalSeconds * 16000);
        var audio = new float[total];
        var flags = new List<bool>();
        using var vad = runtime.CreateVoiceActivityDetector();
        var chunk = new float[vad.ChunkSamples];
        int fill = 0, length = 0;
        float probability = 0;
        await capture.StartAsync(new MicrophoneCaptureOptions(), cancellationToken).ConfigureAwait(false);
        AudioQueueStatistics stats;
        try
        {
            var frame = new float[capture.Frames.FrameSamples];
            while (length + frame.Length <= total)
            {
                var read = await capture.Frames.ReadFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                if (!read.HasFrame)
                {
                    break;
                }

                frame.CopyTo(audio, length);
                length += frame.Length;
                foreach (float s in frame)
                {
                    chunk[fill++] = s;
                    if (fill == chunk.Length)
                    {
                        fill = 0;
                        probability = vad.Process(chunk);
                    }
                }

                flags.Add(probability >= 0.5f);
                level?.Report(new LevelReading(AudioLevel.RmsDbfs(frame), AudioLevel.ToDbfs(AudioLevel.Peak(frame)), probability >= 0.5f, length / 16000.0));
            }

            stats = capture.Frames.GetStatistics();
        }
        finally
        {
            await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var quality = RecordingQualityAnalyzer.Analyze(audio.AsSpan(0, length), 16000, flags,
            new RecordingQualityThresholds { MinSpeechSeconds = 0.5 });
        Array.Clear(audio);
        return new MicrophoneTestResult(quality, capture.ActiveConfiguration, stats);
    }
}
