using System.Collections.Concurrent;
using BonikPOS.VoiceSearch.Audio;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Vad;

namespace BonikPOS.VoiceSearch.IntegrationTests.TestSupport;

/// <summary>
/// STUB speech recogniser: ignores the audio and returns fixed text. No speech-recognition model ships in the
/// repository, and the end-to-end tests need a deterministic search outcome. It is not speech recognition.
/// </summary>
public sealed class StubSpeechRecognizer(string text, double confidence = 0.9) : ISpeechRecognitionService
{
    public ConcurrentQueue<int> Calls { get; } = new();

    public SpeechRecognizerInfo Info { get; } = new(true, "STUB-fixed-text", "STUB recogniser returning fixed text (tests only)", 16000 * 30, "Stub");

    public Task<SpeechRecognitionResult> RecognizeAsync(ReadOnlyMemory<float> audio16k, SpeechRecognitionOptions options, CancellationToken cancellationToken)
    {
        Calls.Enqueue(audio16k.Length);
        return Task.FromResult(new SpeechRecognitionResult(text, "en", confidence, Math.Log(confidence), 0.01, 4,
            TimeSpan.FromSeconds(audio16k.Length / 16000.0), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, Info.ModelId!, false));
    }

    public void Dispose()
    {
    }
}

/// <summary>The real runtime (VAD, speaker encoder, extraction) with only the recogniser replaced by a stub.</summary>
public sealed class RuntimeWithStubRecognizer(IVoiceModelRuntime inner, ISpeechRecognitionService stubRecognizer) : IVoiceModelRuntime
{
    public VoiceModelRuntimeStatus Status => inner.Status;

    public ISpeakerEmbeddingService? SpeakerEncoder => inner.SpeakerEncoder;

    public IOwnerVoiceVerifier? OwnerVerifier => inner.OwnerVerifier;

    public string? ExtractionRequiredEncoderId => inner.ExtractionRequiredEncoderId;

    public ISpeechRecognitionService Recognizer => stubRecognizer;

    public Task InitializeAsync(VoiceSearchSettings settings, CancellationToken cancellationToken) => inner.InitializeAsync(settings, cancellationToken);

    public Task ReloadAsync(VoiceSearchSettings settings, CancellationToken cancellationToken) => inner.ReloadAsync(settings, cancellationToken);

    public IVoiceActivityDetector CreateVoiceActivityDetector() => inner.CreateVoiceActivityDetector();

    public ITargetSpeakerExtractor CreateTargetSpeakerExtractor() => inner.CreateTargetSpeakerExtractor();

    public TargetSpeakerExtractorStatus CreateTargetSpeakerExtractorStatusPreview() => inner.CreateTargetSpeakerExtractorStatusPreview();

    public void Dispose()
    {
        // The inner runtime is owned by the test fixture.
    }
}

/// <summary>
/// A "microphone" that plays one recorded clip per StartAsync through <see cref="FileAudioCapture"/> (non-real-time),
/// e.g. one clip per enrollment sample.
/// </summary>
public sealed class ClipSequenceCapture(params PcmAudio[] clips) : IMicrophoneCapture
{
    private readonly FileAudioCapture[] _captures = [.. clips.Select(c => new FileAudioCapture(c, realTime: false))];
    private int _current = -1;

    private FileAudioCapture Current => _captures[Math.Max(0, _current)];

    public IAudioFrameSource Frames => Current.Frames;

    public MicrophoneCaptureState State => _current < 0 ? MicrophoneCaptureState.Stopped : Current.State;

    public MicrophoneCaptureInfo? ActiveConfiguration => Current.ActiveConfiguration;

    public event EventHandler<MicrophoneCaptureStateChangedEventArgs>? StateChanged;

    public Task StartAsync(MicrophoneCaptureOptions options, CancellationToken cancellationToken)
    {
        _current++;
        Assert.True(_current < _captures.Length, "more recordings were started than clips were provided");
        StateChanged?.Invoke(this, new MicrophoneCaptureStateChangedEventArgs(MicrophoneCaptureState.Running, $"clip {_current}"));
        return Current.StartAsync(options, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Current.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _captures)
        {
            await c.DisposeAsync();
        }
    }
}

/// <summary>Records pipeline events; lets a test await the end of a session without sleeping.</summary>
public sealed class PipelineProbe
{
    private readonly ConcurrentQueue<PipelineEvent> _events = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _listened;

    public PipelineProbe(IVoiceSearchPipeline pipeline) => pipeline.EventRaised += (_, e) =>
    {
        _events.Enqueue(e.Event);
        if (e.Event is PipelineStateChangedEvent { State: PipelineState.Listening })
        {
            _listened = true;
        }
        else if (_listened && e.Event is PipelineStateChangedEvent { State: PipelineState.Idle or PipelineState.Faulted })
        {
            _ended.TrySetResult();
        }
    };

    public IReadOnlyList<PipelineEvent> Events => [.. _events];

    public Task SessionEnded => _ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
}
