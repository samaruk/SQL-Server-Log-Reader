using BonikPOS.VoiceSearch.Core.Audio;

namespace BonikPOS.VoiceSearch.Audio;

/// <summary>
/// Runs on the native capture thread: converts device buffers to 16 kHz mono float, cuts them into fixed frames and
/// pushes them into the <see cref="AudioFrameQueue"/>. Only copies and a short FIR run here - never inference - and
/// all work buffers are preallocated.
/// </summary>
public sealed class CaptureProcessor
{
    public const int PipelineSampleRate = 16000;

    private readonly AudioFrameQueue _queue;
    private readonly PolyphaseResampler? _resampler;
    private readonly float[] _converted;
    private readonly float[] _resampled;
    private readonly float[] _frame;
    private readonly int _maxBlock;
    private int _fill;

    public CaptureProcessor(int deviceSampleRate, AudioFrameQueue queue, int maxCallbackSamples)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.Format.SampleRate != PipelineSampleRate || queue.Format.Channels != 1)
        {
            throw new ArgumentException("The pipeline queue must be 16 kHz mono.", nameof(queue));
        }

        _queue = queue;
        DeviceSampleRate = deviceSampleRate;
        _maxBlock = Math.Max(256, maxCallbackSamples);
        _converted = new float[_maxBlock];
        if (deviceSampleRate != PipelineSampleRate)
        {
            _resampler = new PolyphaseResampler(deviceSampleRate, PipelineSampleRate);
            _resampled = new float[_resampler.MaxOutputFor(_maxBlock)];
        }
        else
        {
            _resampled = [];
        }

        _frame = new float[queue.FrameSamples];
    }

    public int DeviceSampleRate { get; }

    public bool IsResampling => _resampler is not null;

    public long FramesProduced { get; private set; }

    /// <summary>Mono float samples at the device rate.</summary>
    public void Process(ReadOnlySpan<float> mono, long captureTimestamp)
    {
        while (!mono.IsEmpty)
        {
            int n = Math.Min(mono.Length, _maxBlock);
            ProcessBlock(mono[..n], captureTimestamp);
            mono = mono[n..];
        }
    }

    /// <summary>Mono 16-bit PCM samples at the device rate.</summary>
    public void Process(ReadOnlySpan<short> mono, long captureTimestamp)
    {
        while (!mono.IsEmpty)
        {
            int n = Math.Min(mono.Length, _maxBlock);
            for (int i = 0; i < n; i++)
            {
                _converted[i] = mono[i] / 32768f;
            }

            ProcessBlock(_converted.AsSpan(0, n), captureTimestamp);
            mono = mono[n..];
        }
    }

    /// <summary>Interleaved multi-channel float samples: channel 0 is used.</summary>
    public void ProcessInterleaved(ReadOnlySpan<float> interleaved, int channels, long captureTimestamp)
    {
        if (channels == 1)
        {
            Process(interleaved, captureTimestamp);
            return;
        }

        int frames = interleaved.Length / channels;
        int offset = 0;
        while (offset < frames)
        {
            int n = Math.Min(frames - offset, _maxBlock);
            for (int i = 0; i < n; i++)
            {
                _converted[i] = interleaved[(offset + i) * channels];
            }

            ProcessBlock(_converted.AsSpan(0, n), captureTimestamp);
            offset += n;
        }
    }

    public void Reset()
    {
        _resampler?.Reset();
        _fill = 0;
    }

    private void ProcessBlock(ReadOnlySpan<float> block, long timestamp)
    {
        ReadOnlySpan<float> pcm = block;
        if (_resampler is not null)
        {
            int produced = _resampler.Process(block, _resampled);
            pcm = _resampled.AsSpan(0, produced);
        }

        while (!pcm.IsEmpty)
        {
            int take = Math.Min(pcm.Length, _frame.Length - _fill);
            pcm[..take].CopyTo(_frame.AsSpan(_fill));
            _fill += take;
            pcm = pcm[take..];
            if (_fill == _frame.Length)
            {
                _queue.TryWrite(_frame, timestamp);
                FramesProduced++;
                _fill = 0;
            }
        }
    }
}
