using Android.Media;
using BonikPOS.VoiceSearch.Core.Audio;
using AudioEncoding = Android.Media.Encoding;

namespace BonikPOS.VoiceSearch.Audio.Platforms.Android;

/// <summary>Plays short mono clips through android.media.AudioTrack (static mode).</summary>
public sealed class AndroidAudioOutput : IAudioOutput
{
    private volatile bool _playing;

    public bool IsPlaying => _playing;

    public async Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRate, CancellationToken cancellationToken)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        var data = samples.ToArray();
        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!
            .Build();
        var format = new AudioFormat.Builder()
            .SetEncoding(AudioEncoding.PcmFloat)!
            .SetSampleRate(sampleRate)!
            .SetChannelMask(ChannelOut.Mono)!
            .Build();
        using var track = new AudioTrack.Builder()
            .SetAudioAttributes(attributes!)
            .SetAudioFormat(format!)
            .SetTransferMode(AudioTrackMode.Static)
            .SetBufferSizeInBytes(data.Length * sizeof(float))
            .Build();
        try
        {
            _playing = true;
            track!.Write(data, 0, data.Length, WriteMode.Blocking);
            track.Play();
            var duration = TimeSpan.FromSeconds((double)data.Length / sampleRate);
            await Task.Delay(duration + TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                track?.Stop();
            }
            catch (Java.Lang.IllegalStateException)
            {
            }

            track?.Release();
            _playing = false;
        }
    }
}
