using AVFoundation;
using Foundation;
using BonikPOS.VoiceSearch.Core.Audio;

namespace BonikPOS.VoiceSearch.Audio.Platforms.iOS;

/// <summary>Plays short mono clips with AVAudioEngine + AVAudioPlayerNode.</summary>
public sealed class IosAudioOutput : IAudioOutput
{
    private volatile bool _playing;

    public bool IsPlaying => _playing;

    public async Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRate, CancellationToken cancellationToken)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        using var engine = new AVAudioEngine();
        using var player = new AVAudioPlayerNode();
        using var format = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, sampleRate, 1, false);
        using var buffer = new AVAudioPcmBuffer(format, (uint)samples.Length);
        buffer.FrameLength = (uint)samples.Length;
        unsafe
        {
            float* channel0 = ((float**)buffer.FloatChannelData)[0];
            samples.Span.CopyTo(new Span<float>(channel0, samples.Length));
        }

        engine.AttachNode(player);
        if (OperatingSystem.IsIOSVersionAtLeast(27))
        {
            if (!engine.Connect(player, engine.MainMixerNode, format, out NSError? connectError))
            {
                throw new InvalidOperationException($"Playback graph could not be connected: {connectError?.LocalizedDescription}");
            }
        }
        else
        {
#pragma warning disable CA1422 // the non-throwing overload is the only one before iOS 27
            engine.Connect(player, engine.MainMixerNode, format);
#pragma warning restore CA1422
        }

        engine.Prepare();
        if (!engine.StartAndReturnError(out var error))
        {
            throw new InvalidOperationException($"Playback engine could not start: {error?.LocalizedDescription}");
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _playing = true;
            player.ScheduleBuffer(buffer, () => done.TrySetResult());
            if (OperatingSystem.IsIOSVersionAtLeast(27))
            {
                if (!player.Play(out NSError? playError))
                {
                    throw new InvalidOperationException($"Playback could not start: {playError?.LocalizedDescription}");
                }
            }
            else
            {
#pragma warning disable CA1422 // the non-throwing overload is the only one before iOS 27
                player.Play();
#pragma warning restore CA1422
            }
            using (cancellationToken.Register(() => done.TrySetCanceled(cancellationToken)))
            {
                await done.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            player.Stop();
            engine.Stop();
            _playing = false;
        }
    }
}
