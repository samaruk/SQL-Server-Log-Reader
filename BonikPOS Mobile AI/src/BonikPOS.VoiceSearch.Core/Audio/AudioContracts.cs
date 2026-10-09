namespace BonikPOS.VoiceSearch.Core.Audio;

/// <summary>PCM stream format. The processing pipeline works on 16 kHz, mono, float32 samples in [-1, 1].</summary>
public readonly record struct AudioStreamFormat(int SampleRate, int Channels)
{
    /// <summary>The format every model in this solution consumes.</summary>
    public static AudioStreamFormat Speech16kMono { get; } = new(16000, 1);

    public int SamplesFor(TimeSpan duration) => (int)Math.Round(duration.TotalSeconds * SampleRate);

    public TimeSpan DurationOf(long samples) => TimeSpan.FromSeconds((double)samples / SampleRate);
}

/// <summary>Metadata of one frame delivered by an <see cref="IAudioFrameSource"/>.</summary>
/// <param name="Sequence">Monotonic frame number assigned by the producer (gaps mean dropped frames).</param>
/// <param name="CaptureTimestamp">Stopwatch timestamp taken when the native callback delivered the audio.</param>
/// <param name="Discontinuity">True when frames were dropped before this frame; streaming model state should be reset.</param>
/// <param name="DroppedFramesBefore">Number of frames dropped between the previous frame read and this one.</param>
public readonly record struct AudioFrameInfo(long Sequence, long CaptureTimestamp, bool Discontinuity, int DroppedFramesBefore);

public readonly record struct AudioFrameReadResult(bool HasFrame, AudioFrameInfo Info)
{
    public static AudioFrameReadResult Completed => new(false, default);
}

/// <summary>Counters of the bounded capture queue. Never contains audio.</summary>
public readonly record struct AudioQueueStatistics(
    long FramesWritten, long FramesRead, long FramesDropped, int Depth, int MaxDepth, int Capacity);

/// <summary>
/// Consumer side of the bounded producer/consumer audio queue. Frames have a fixed size of
/// <see cref="FrameSamples"/> samples in <see cref="Format"/>.
/// </summary>
public interface IAudioFrameSource
{
    AudioStreamFormat Format { get; }

    int FrameSamples { get; }

    /// <summary>
    /// Copies the next frame into <paramref name="destination"/> (length must be at least <see cref="FrameSamples"/>).
    /// Waits until a frame is available. Returns <see cref="AudioFrameReadResult.Completed"/> once the producer has
    /// completed and the queue is drained.
    /// </summary>
    ValueTask<AudioFrameReadResult> ReadFrameAsync(Memory<float> destination, CancellationToken cancellationToken);

    AudioQueueStatistics GetStatistics();
}

public enum MicrophoneCaptureState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    /// <summary>The OS interrupted capture (phone call, another app took the microphone).</summary>
    Interrupted,
    Faulted,
}

/// <summary>Which platform input path to request.</summary>
public enum AudioInputPreference
{
    /// <summary>Android VOICE_RECOGNITION / iOS measurement mode: tuned for speech recognition, minimal processing.</summary>
    VoiceRecognition,
    /// <summary>Android UNPROCESSED when the device supports it (falls back to VoiceRecognition).</summary>
    Unprocessed,
    /// <summary>The platform default microphone path (may include AGC / noise suppression).</summary>
    Default,
}

public sealed record MicrophoneCaptureOptions
{
    /// <summary>Frame length delivered to the pipeline. 20 ms = 320 samples at 16 kHz.</summary>
    public int FrameMilliseconds { get; init; } = 20;

    /// <summary>Bounded queue capacity in frames. When full, the OLDEST frame is dropped (see docs/ARCHITECTURE.md).</summary>
    public int QueueCapacityFrames { get; init; } = 150;

    public AudioInputPreference InputPreference { get; init; } = AudioInputPreference.VoiceRecognition;
}

/// <summary>The configuration the platform actually granted.</summary>
public sealed record MicrophoneCaptureInfo(
    int DeviceSampleRate, int DeviceChannels, int CallbackFrames, string InputPath, bool SoftwareResampling);

public sealed class MicrophoneCaptureStateChangedEventArgs(MicrophoneCaptureState state, string? detail) : EventArgs
{
    public MicrophoneCaptureState State { get; } = state;

    public string? Detail { get; } = detail;
}

/// <summary>Native microphone capture behind a common interface (Android AudioRecord, iOS AVAudioEngine).</summary>
/// <remarks>
/// Implementations must never run inference or block inside the native audio callback: they only convert the
/// samples and copy them into the bounded queue exposed by <see cref="Frames"/>.
/// </remarks>
public interface IMicrophoneCapture : IAsyncDisposable
{
    IAudioFrameSource Frames { get; }

    MicrophoneCaptureState State { get; }

    MicrophoneCaptureInfo? ActiveConfiguration { get; }

    /// <summary>
    /// Latest estimate of the platform input latency in milliseconds: from the sound reaching the microphone's
    /// converter to the newest sample being handed to the app (Android: AudioRecord.getTimestamp; iOS: buffer host
    /// time plus AVAudioSession.inputLatency). Null when the platform does not report it.
    /// </summary>
    double? InputLatencyMilliseconds => null;

    event EventHandler<MicrophoneCaptureStateChangedEventArgs>? StateChanged;

    /// <summary>Starts capture. Throws <see cref="MicrophonePermissionDeniedException"/> if access is not granted.</summary>
    Task StartAsync(MicrophoneCaptureOptions options, CancellationToken cancellationToken);

    /// <summary>Stops capture, completes <see cref="Frames"/> and releases the device.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Plays short PCM clips (prompt tones, playback of a just-recorded enrollment sample held in memory).</summary>
public interface IAudioOutput
{
    bool IsPlaying { get; }

    /// <summary>Plays mono float PCM; completes when playback ends or <paramref name="cancellationToken"/> fires.</summary>
    Task PlayAsync(ReadOnlyMemory<float> samples, int sampleRate, CancellationToken cancellationToken);
}

public sealed class MicrophonePermissionDeniedException(string message) : InvalidOperationException(message);

public sealed class MicrophoneUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
