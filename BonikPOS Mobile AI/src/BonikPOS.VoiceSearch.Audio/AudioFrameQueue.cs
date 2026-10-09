using System.Collections.Concurrent;
using System.Threading.Channels;
using BonikPOS.VoiceSearch.Core.Audio;

namespace BonikPOS.VoiceSearch.Audio;

/// <summary>
/// Bounded single-producer / single-consumer frame queue between the native capture thread and the pipeline.
/// </summary>
/// <remarks>
/// Overrun policy: when the consumer falls behind and the queue is full, the OLDEST frame is dropped so latency
/// stays bounded and the freshest audio is processed. Every drop is counted, and the next frame the consumer reads
/// carries <see cref="AudioFrameInfo.Discontinuity"/> so streaming models can reset their state.
/// Frame buffers come from a preallocated pool, so steady-state operation does not allocate.
/// </remarks>
public sealed class AudioFrameQueue : IAudioFrameSource
{
    private readonly Channel<Frame> _channel;
    private readonly ConcurrentQueue<float[]> _pool = new();
    private long _nextSequence;
    private long _expectedSequence;
    private long _written;
    private long _read;
    private long _dropped;
    private int _maxDepth;

    public AudioFrameQueue(AudioStreamFormat format, int frameSamples, int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameSamples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 2);
        Format = format;
        FrameSamples = frameSamples;
        Capacity = capacityFrames;
        for (int i = 0; i < capacityFrames + 2; i++)
        {
            _pool.Enqueue(new float[frameSamples]);
        }

        _channel = Channel.CreateBounded<Frame>(
            new BoundedChannelOptions(capacityFrames)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            },
            OnDropped);
    }

    public AudioStreamFormat Format { get; }

    public int FrameSamples { get; }

    public int Capacity { get; }

    /// <summary>Producer side. Never blocks; drops the oldest frame when full. Returns false after <see cref="Complete"/>.</summary>
    public bool TryWrite(ReadOnlySpan<float> frame, long captureTimestamp)
    {
        if (frame.Length != FrameSamples)
        {
            throw new ArgumentException($"Frame must have exactly {FrameSamples} samples.", nameof(frame));
        }

        if (!_pool.TryDequeue(out var buffer))
        {
            buffer = new float[FrameSamples];
        }

        frame.CopyTo(buffer);
        var item = new Frame(buffer, _nextSequence++, captureTimestamp);
        if (!_channel.Writer.TryWrite(item))
        {
            _pool.Enqueue(buffer);
            return false;
        }

        long written = Interlocked.Increment(ref _written);
        int depth = (int)(written - Interlocked.Read(ref _read) - Interlocked.Read(ref _dropped));
        if (depth > Volatile.Read(ref _maxDepth))
        {
            Volatile.Write(ref _maxDepth, depth);
        }

        return true;
    }

    /// <summary>Marks the end of the stream; the consumer drains remaining frames and then sees completion.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public async ValueTask<AudioFrameReadResult> ReadFrameAsync(Memory<float> destination, CancellationToken cancellationToken)
    {
        if (destination.Length < FrameSamples)
        {
            throw new ArgumentException($"Destination must hold at least {FrameSamples} samples.", nameof(destination));
        }

        var reader = _channel.Reader;
        while (true)
        {
            if (reader.TryRead(out var frame))
            {
                frame.Buffer.AsSpan().CopyTo(destination.Span);
                _pool.Enqueue(frame.Buffer);
                Interlocked.Increment(ref _read);
                long gap = frame.Sequence - _expectedSequence;
                _expectedSequence = frame.Sequence + 1;
                var info = new AudioFrameInfo(frame.Sequence, frame.CaptureTimestamp, gap > 0, (int)Math.Max(0, gap));
                return new AudioFrameReadResult(true, info);
            }

            if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return AudioFrameReadResult.Completed;
            }
        }
    }

    public AudioQueueStatistics GetStatistics()
    {
        long written = Interlocked.Read(ref _written);
        long read = Interlocked.Read(ref _read);
        long dropped = Interlocked.Read(ref _dropped);
        return new AudioQueueStatistics(written, read, dropped, (int)(written - read - dropped), Volatile.Read(ref _maxDepth), Capacity);
    }

    private void OnDropped(Frame frame)
    {
        Interlocked.Increment(ref _dropped);
        _pool.Enqueue(frame.Buffer);
    }

    private readonly record struct Frame(float[] Buffer, long Sequence, long CaptureTimestamp);
}
