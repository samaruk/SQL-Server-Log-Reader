using System.Diagnostics;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using BonikPOS.VoiceSearch.Core.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AndroidProcess = Android.OS.Process;
using AudioEncoding = Android.Media.Encoding;

namespace BonikPOS.VoiceSearch.Audio.Platforms.Android;

/// <summary>
/// Microphone capture with android.media.AudioRecord on a dedicated URGENT_AUDIO thread. The thread only reads,
/// converts and enqueues; all inference happens on the pipeline workers.
/// </summary>
public sealed class AndroidMicrophoneCapture : IMicrophoneCapture
{
    // android.media.AudioRecord.READ_BLOCKING (the binding exposes readMode as int).
    private const int ReadBlocking = 0;

    // Input-latency probe every 10 reads (100 ms); AudioRecord.getTimestamp reads shared memory and does not block.
    private const int LatencyProbeEveryReads = 10;
    private static readonly int[] CandidateRates = [16000, 48000, 44100];

    private readonly Context _context;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private AudioFrameQueue _queue = new(AudioStreamFormat.Speech16kMono, 320, 150);
    private AudioRecord? _record;
    private Thread? _thread;
    private volatile bool _running;
    private long _inputLatencyMicros = -1; // written by the capture thread, -1 = not reported

    public AndroidMicrophoneCapture(Context context, ILogger<AndroidMicrophoneCapture>? logger = null)
    {
        _context = context;
        _logger = logger ?? NullLogger<AndroidMicrophoneCapture>.Instance;
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

            if (_context.CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            {
                throw new MicrophonePermissionDeniedException("Microphone permission has not been granted.");
            }

            SetState(MicrophoneCaptureState.Starting, null);
            int frameSamples = CaptureProcessor.PipelineSampleRate * options.FrameMilliseconds / 1000;
            _queue = new AudioFrameQueue(AudioStreamFormat.Speech16kMono, frameSamples, options.QueueCapacityFrames);

            var (source, sourceName) = ChooseSource(options.InputPreference);
            AudioRecord? record = null;
            int rate = 0;
            bool isFloat = true;
            foreach (int candidate in CandidateRates)
            {
                record = TryCreate(source, candidate, AudioEncoding.PcmFloat) ?? TryCreate(source, candidate, AudioEncoding.Pcm16bit);
                if (record is not null)
                {
                    rate = candidate;
                    isFloat = record.AudioFormat == AudioEncoding.PcmFloat;
                    break;
                }
            }

            if (record is null)
            {
                SetState(MicrophoneCaptureState.Faulted, "No supported microphone configuration.");
                throw new MicrophoneUnavailableException("The microphone could not be opened with any supported configuration.");
            }

            record.StartRecording();
            if (record.RecordingState != RecordState.Recording)
            {
                record.Release();
                SetState(MicrophoneCaptureState.Faulted, "Microphone busy.");
                throw new MicrophoneUnavailableException("The microphone is in use by another app.");
            }

            int callbackSamples = rate / 100; // 10 ms reads
            var processor = new CaptureProcessor(rate, _queue, callbackSamples);
            _record = record;
            _running = true;
            Interlocked.Exchange(ref _inputLatencyMicros, -1);
            ActiveConfiguration = new MicrophoneCaptureInfo(rate, 1, callbackSamples, sourceName + (isFloat ? "/float" : "/pcm16"), processor.IsResampling);
            _thread = new Thread(() => ReadLoop(record, processor, callbackSamples, isFloat, rate))
            {
                IsBackground = true,
                Name = "BonikPOS.AudioCapture",
            };
            _thread.Start();
            SetState(MicrophoneCaptureState.Running, ActiveConfiguration.ToString());
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Thread? thread;
        AudioRecord? record;
        lock (_gate)
        {
            if (State is MicrophoneCaptureState.Stopped)
            {
                return Task.CompletedTask;
            }

            SetState(MicrophoneCaptureState.Stopping, null);
            _running = false;
            thread = _thread;
            record = _record;
            _thread = null;
            _record = null;
        }

        return Task.Run(
            () =>
            {
                try
                {
                    record?.Stop(); // unblocks the blocking read
                }
                catch (Java.Lang.IllegalStateException ex)
                {
                    _logger.LogDebug(ex, "AudioRecord.stop on a stopped recorder");
                }

                thread?.Join(TimeSpan.FromSeconds(2));
                record?.Release();
                record?.Dispose();
                _queue.Complete();
                lock (_gate)
                {
                    SetState(MicrophoneCaptureState.Stopped, null);
                }
            },
            CancellationToken.None);
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void ReadLoop(AudioRecord record, CaptureProcessor processor, int callbackSamples, bool isFloat, int sampleRate)
    {
        try
        {
            AndroidProcess.SetThreadPriority(global::Android.OS.ThreadPriority.UrgentAudio);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not raise capture thread priority");
        }

        var floats = new float[callbackSamples];
        var shorts = new short[callbackSamples];
        var audioTimestamp = OperatingSystem.IsAndroidVersionAtLeast(24) ? new AudioTimestamp() : null;
        long framesRead = 0;
        int reads = 0;
        while (_running)
        {
            int n = isFloat
                ? record.Read(floats, 0, floats.Length, ReadBlocking)
                : record.Read(shorts, 0, shorts.Length, ReadBlocking);
            if (n < 0)
            {
                if (_running)
                {
                    _logger.LogWarning("AudioRecord.read returned error {Code}", n);
                    _running = false;
                    lock (_gate)
                    {
                        SetState(MicrophoneCaptureState.Interrupted, $"AudioRecord error {n}");
                    }

                    _queue.Complete();
                }

                break;
            }

            if (n == 0)
            {
                continue;
            }

            long timestamp = Stopwatch.GetTimestamp();
            if (isFloat)
            {
                processor.Process(floats.AsSpan(0, n), timestamp);
            }
            else
            {
                processor.Process(shorts.AsSpan(0, n), timestamp);
            }

            framesRead += n; // mono: one sample per frame
            if (audioTimestamp is not null && ++reads % LatencyProbeEveryReads == 0)
            {
                ProbeInputLatency(record, audioTimestamp, framesRead, sampleRate);
            }
        }
    }

    /// <summary>
    /// Input latency of the newest sample read: now minus the time its frame was captured, extrapolated from the
    /// (frame position, CLOCK_MONOTONIC time) pair AudioRecord.getTimestamp reports. Implausible values are ignored.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("android24.0")]
    private void ProbeInputLatency(AudioRecord record, AudioTimestamp audioTimestamp, long framesRead, int sampleRate)
    {
        try
        {
            if (record.GetTimestamp(audioTimestamp, AudioTimebase.Monotonic) != 0)
            {
                return; // not available on this device / route yet
            }

            long capturedNs = audioTimestamp.NanoTime + (framesRead - audioTimestamp.FramePosition) * 1_000_000_000L / sampleRate;
            long latencyUs = (Java.Lang.JavaSystem.NanoTime() - capturedNs) / 1000;
            if (latencyUs is >= 0 and < 2_000_000)
            {
                Interlocked.Exchange(ref _inputLatencyMicros, latencyUs);
            }
        }
        catch (Java.Lang.Exception)
        {
            // Some vendor implementations throw instead of returning an error code; the estimate stays unavailable.
        }
    }

    private (AudioSource Source, string Name) ChooseSource(AudioInputPreference preference)
    {
        if (preference == AudioInputPreference.Unprocessed)
        {
            var manager = (AudioManager?)_context.GetSystemService(Context.AudioService);
            if (string.Equals(manager?.GetProperty(AudioManager.PropertySupportAudioSourceUnprocessed), "true", StringComparison.OrdinalIgnoreCase))
            {
                return (AudioSource.Unprocessed, "UNPROCESSED");
            }
        }

        return preference == AudioInputPreference.Default
            ? (AudioSource.Mic, "MIC")
            : (AudioSource.VoiceRecognition, "VOICE_RECOGNITION");
    }

    private AudioRecord? TryCreate(AudioSource source, int rate, AudioEncoding encoding)
    {
        int minBuffer = AudioRecord.GetMinBufferSize(rate, ChannelIn.Mono, encoding);
        if (minBuffer <= 0)
        {
            return null;
        }

        int bytesPerSample = encoding == AudioEncoding.PcmFloat ? 4 : 2;
        int bufferBytes = Math.Max(minBuffer, rate * bytesPerSample / 5); // at least 200 ms of device buffering
        try
        {
            var format = new AudioFormat.Builder()
                .SetEncoding(encoding)!
                .SetSampleRate(rate)!
                .SetChannelMask((ChannelOut)(int)ChannelIn.Mono)! // binding types this as ChannelOut; the value is CHANNEL_IN_MONO
                .Build();
            var record = new AudioRecord.Builder()
                .SetAudioSource(source)!
                .SetAudioFormat(format!)!
                .SetBufferSizeInBytes(bufferBytes)!
                .Build();
            if (record?.State == global::Android.Media.State.Initialized)
            {
                return record;
            }

            record?.Release();
        }
        catch (Exception ex) when (ex is Java.Lang.UnsupportedOperationException or Java.Lang.IllegalArgumentException)
        {
            _logger.LogDebug(ex, "AudioRecord {Rate} Hz {Encoding} not supported", rate, encoding);
        }

        return null;
    }

    private void SetState(MicrophoneCaptureState state, string? detail)
    {
        State = state;
        StateChanged?.Invoke(this, new MicrophoneCaptureStateChangedEventArgs(state, detail));
    }
}
