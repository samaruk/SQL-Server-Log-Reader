using System.Diagnostics;
using System.Runtime.InteropServices;
using AVFoundation;
using BonikPOS.VoiceSearch.Core.Audio;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonikPOS.VoiceSearch.Audio.Platforms.iOS;

/// <summary>
/// Microphone capture with AVAudioEngine. The input-node tap only copies channel 0 into the bounded queue (through
/// <see cref="CaptureProcessor"/>, which also converts 48 kHz hardware audio to 16 kHz).
/// </summary>
/// <remarks>
/// AVAudioEngine taps deliver buffers whose size iOS chooses (typically 100 ms or more, regardless of the requested
/// size); the pipeline re-frames them into 20 ms frames. The session uses <c>AVAudioSessionMode.Measurement</c> for
/// speech recognition so that iOS applies minimal processing, unless the owner selects the default input path.
/// </remarks>
public sealed partial class IosMicrophoneCapture : IMicrophoneCapture
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private AudioFrameQueue _queue = new(AudioStreamFormat.Speech16kMono, 320, 150);
    private AVAudioEngine? _engine;
    private CaptureProcessor? _processor;
    private NSObject? _interruptionObserver;
    private NSObject? _configurationObserver;
    private long _inputLatencyMicros = -1; // written by the tap thread, -1 = not reported
    private double _hardwareInputLatencySeconds; // AVAudioSession.inputLatency of the current route
    private int _tapSampleRate;

    public IosMicrophoneCapture(ILogger<IosMicrophoneCapture>? logger = null)
    {
        _logger = logger ?? NullLogger<IosMicrophoneCapture>.Instance;
    }

    public IAudioFrameSource Frames => _queue;

    public MicrophoneCaptureState State { get; private set; } = MicrophoneCaptureState.Stopped;

    public MicrophoneCaptureInfo? ActiveConfiguration { get; private set; }

    public double? InputLatencyMilliseconds => Interlocked.Read(ref _inputLatencyMicros) is var us and >= 0 ? us / 1000.0 : null;

    public event EventHandler<MicrophoneCaptureStateChangedEventArgs>? StateChanged;

    public Task StartAsync(MicrophoneCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (State is MicrophoneCaptureState.Running or MicrophoneCaptureState.Starting)
            {
                throw new InvalidOperationException("Capture is already running.");
            }

            if (!HasRecordPermission())
            {
                throw new MicrophonePermissionDeniedException("Microphone permission has not been granted.");
            }

            SetState(MicrophoneCaptureState.Starting, null);
            int frameSamples = CaptureProcessor.PipelineSampleRate * options.FrameMilliseconds / 1000;
            _queue = new AudioFrameQueue(AudioStreamFormat.Speech16kMono, frameSamples, options.QueueCapacityFrames);

            ConfigureSession(options.InputPreference);
            try
            {
                StartEngine();
            }
            catch
            {
                DeactivateSession();
                SetState(MicrophoneCaptureState.Faulted, "Audio engine failed to start.");
                throw;
            }

            _interruptionObserver = AVAudioSession.Notifications.ObserveInterruption(OnInterruption);
            _configurationObserver = AVAudioEngine.Notifications.ObserveConfigurationChange(OnConfigurationChange);
            SetState(MicrophoneCaptureState.Running, ActiveConfiguration?.ToString());
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (State is MicrophoneCaptureState.Stopped)
            {
                return Task.CompletedTask;
            }

            SetState(MicrophoneCaptureState.Stopping, null);
            _interruptionObserver?.Dispose();
            _configurationObserver?.Dispose();
            _interruptionObserver = null;
            _configurationObserver = null;
            StopEngine();
            DeactivateSession();
            _queue.Complete();
            SetState(MicrophoneCaptureState.Stopped, null);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private static bool HasRecordPermission()
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17))
        {
            return AVAudioApplication.SharedInstance.RecordPermission == AVAudioApplicationRecordPermission.Granted;
        }

#pragma warning disable CA1422 // AVAudioSession.RecordPermission is the API for iOS 16
        return AVAudioSession.SharedInstance().RecordPermission == AVAudioSessionRecordPermission.Granted;
#pragma warning restore CA1422
    }

    private void ConfigureSession(AudioInputPreference preference)
    {
        var session = AVAudioSession.SharedInstance();
        var mode = preference == AudioInputPreference.Default ? AVAudioSessionMode.Default : AVAudioSessionMode.Measurement;
        if (!session.SetCategory(AVAudioSessionCategory.PlayAndRecord, mode, AVAudioSessionCategoryOptions.DefaultToSpeaker, out NSError? error))
        {
            throw new MicrophoneUnavailableException($"Audio session category could not be set: {error?.LocalizedDescription}");
        }

        // A short IO buffer lowers latency where the hardware allows it; failure is not fatal.
        if (!session.SetPreferredIOBufferDuration(0.01, out error))
        {
            _logger.LogDebug("Preferred IO buffer duration rejected: {Error}", error?.LocalizedDescription);
        }

        if (!session.SetActive(true, out error))
        {
            throw new MicrophoneUnavailableException($"Audio session could not be activated: {error?.LocalizedDescription}");
        }
    }

    private void StartEngine()
    {
        var engine = new AVAudioEngine();
        var input = engine.InputNode;
        var format = input.GetBusOutputFormat(0);
        if (format.SampleRate <= 0 || format.ChannelCount == 0)
        {
            engine.Dispose();
            throw new MicrophoneUnavailableException("No audio input is available.");
        }

        int rate = (int)Math.Round(format.SampleRate);
        var processor = new CaptureProcessor(rate, _queue, maxCallbackSamples: rate / 5);
        uint requestedFrames = (uint)(rate / 50);
        InstallTap(input, requestedFrames, format);
        engine.Prepare();
        if (!engine.StartAndReturnError(out NSError? error))
        {
            input.RemoveTapOnBus(0);
            engine.Dispose();
            throw new MicrophoneUnavailableException($"Audio engine could not start: {error?.LocalizedDescription}");
        }

        _engine = engine;
        _processor = processor;
        Volatile.Write(ref _hardwareInputLatencySeconds, AVAudioSession.SharedInstance().InputLatency);
        Volatile.Write(ref _tapSampleRate, rate);
        Interlocked.Exchange(ref _inputLatencyMicros, -1);
        ActiveConfiguration = new MicrophoneCaptureInfo(rate, (int)format.ChannelCount, (int)requestedFrames, "AVAudioEngine input tap", processor.IsResampling);
    }

    private void InstallTap(AVAudioInputNode input, uint frames, AVAudioFormat format)
    {
        if (OperatingSystem.IsIOSVersionAtLeast(27))
        {
            if (!input.InstallTapOnBus(0, frames, format, out NSError? error, OnTap))
            {
                throw new MicrophoneUnavailableException($"Input tap could not be installed: {error?.LocalizedDescription}");
            }

            return;
        }

#pragma warning disable CA1422 // the non-throwing overload is the only one before iOS 27
        input.InstallTapOnBus(0, frames, format, OnTap);
#pragma warning restore CA1422
    }

    private void StopEngine()
    {
        var engine = _engine;
        _engine = null;
        if (engine is null)
        {
            return;
        }

        try
        {
            engine.InputNode.RemoveTapOnBus(0);
            engine.Stop();
        }
        finally
        {
            engine.Dispose();
        }
    }

    private void DeactivateSession()
    {
        var error = AVAudioSession.SharedInstance().SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation);
        if (error is not null)
        {
            _logger.LogDebug("Audio session deactivation: {Error}", error.LocalizedDescription);
        }
    }

    // Runs on an AVAudioEngine internal thread: copy only.
    private void OnTap(AVAudioPcmBuffer buffer, AVAudioTime when)
    {
        long timestamp = Stopwatch.GetTimestamp();
        var processor = _processor;
        int frames = (int)buffer.FrameLength;
        IntPtr channels = buffer.FloatChannelData;
        if (processor is null || frames == 0 || channels == IntPtr.Zero)
        {
            return;
        }

        unsafe
        {
            float* channel0 = ((float**)channels)[0];
            processor.Process(new ReadOnlySpan<float>(channel0, frames), timestamp);
        }

        // Input latency of the newest sample: time since the buffer's first sample (host time) minus the buffer
        // duration, plus the route's hardware input latency. Arithmetic only, no allocation.
        int rate = Volatile.Read(ref _tapSampleRate);
        if (when.HostTimeValid && rate > 0)
        {
            double latency = AVAudioTime.SecondsForHostTime(MachAbsoluteTime() - when.HostTime) - (double)frames / rate
                             + Volatile.Read(ref _hardwareInputLatencySeconds);
            if (latency is >= 0 and < 2)
            {
                Interlocked.Exchange(ref _inputLatencyMicros, (long)(latency * 1_000_000));
            }
        }
    }

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "mach_absolute_time")]
    private static partial ulong MachAbsoluteTime();

    private void OnInterruption(object? sender, AVAudioSessionInterruptionEventArgs e)
    {
        lock (_gate)
        {
            if (e.InterruptionType == AVAudioSessionInterruptionType.Began)
            {
                SetState(MicrophoneCaptureState.Interrupted, "Audio interrupted by the system (call, alarm or another app).");
                return;
            }

            if (State == MicrophoneCaptureState.Interrupted)
            {
                TryRestart("Interruption ended");
            }
        }
    }

    private void OnConfigurationChange(object? sender, NSNotificationEventArgs e)
    {
        lock (_gate)
        {
            if (State is MicrophoneCaptureState.Running or MicrophoneCaptureState.Interrupted)
            {
                TryRestart("Audio route changed");
            }
        }
    }

    private void TryRestart(string reason)
    {
        try
        {
            StopEngine();
            _processor?.Reset();
            var session = AVAudioSession.SharedInstance();
            if (!session.SetActive(true, out NSError? error))
            {
                throw new MicrophoneUnavailableException($"Audio session could not be re-activated: {error?.LocalizedDescription}");
            }

            StartEngine();
            SetState(MicrophoneCaptureState.Running, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restarting capture failed");
            SetState(MicrophoneCaptureState.Faulted, ex.Message);
            _queue.Complete();
        }
    }

    private void SetState(MicrophoneCaptureState state, string? detail)
    {
        State = state;
        StateChanged?.Invoke(this, new MicrophoneCaptureStateChangedEventArgs(state, detail));
    }
}
