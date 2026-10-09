using System.Collections.Concurrent;
using BonikPOS.VoiceSearch.AI.Extraction;
using BonikPOS.VoiceSearch.Audio;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Vad;

namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>
/// Microphone fake backed by a real <see cref="AudioFrameQueue"/>. Frames are only pushed when the test calls
/// <see cref="Release"/> (typically once the pipeline reports Listening), so the timing of a test never depends on how
/// fast a background thread happens to run.
/// </summary>
public sealed class TestCapture(float[] signal16k) : IMicrophoneCapture
{
    private AudioFrameQueue _queue = new(AudioStreamFormat.Speech16kMono, 320, 2);

    public IAudioFrameSource Frames => _queue;

    public MicrophoneCaptureState State { get; private set; } = MicrophoneCaptureState.Stopped;

    public MicrophoneCaptureInfo? ActiveConfiguration { get; private set; }

    public MicrophoneCaptureOptions? LastOptions { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public event EventHandler<MicrophoneCaptureStateChangedEventArgs>? StateChanged;

    public Task StartAsync(MicrophoneCaptureOptions options, CancellationToken cancellationToken)
    {
        LastOptions = options;
        StartCount++;
        _released = 0;
        _queue = new AudioFrameQueue(AudioStreamFormat.Speech16kMono, 16000 * options.FrameMilliseconds / 1000, options.QueueCapacityFrames);
        ActiveConfiguration = new MicrophoneCaptureInfo(16000, 1, _queue.FrameSamples, "test", false);
        State = MicrophoneCaptureState.Running;
        StateChanged?.Invoke(this, new MicrophoneCaptureStateChangedEventArgs(State, null));
        return Task.CompletedTask;
    }

    private int _released;

    /// <summary>Pushes the whole remaining signal (whole frames only) and completes the stream. Frames beyond the queue capacity are dropped.</summary>
    public void Release() => ReleaseUntil(signal16k.Length, complete: true);

    /// <summary>Pushes the frames that end at or before <paramref name="sample"/>.</summary>
    public void ReleaseUntil(int sample, bool complete = false)
    {
        lock (_releaseGate) // releases may be triggered from different pipeline threads; the queue has a single producer
        {
            int frame = _queue.FrameSamples;
            while (_released + frame <= Math.Min(sample, signal16k.Length))
            {
                _queue.TryWrite(signal16k.AsSpan(_released, frame), Core.Common.Ticks.Now());
                _released += frame;
            }

            if (complete)
            {
                _queue.Complete();
            }
        }
    }

    private readonly Lock _releaseGate = new();

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        _queue.Complete();
        State = MicrophoneCaptureState.Stopped;
        StateChanged?.Invoke(this, new MicrophoneCaptureStateChangedEventArgs(State, null));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}

/// <summary>Verifier fake returning a fixed decision and recording the speech it was given.</summary>
public sealed class ScriptedVerifier(OwnerVoiceDecision decision, double similarity = 0.9) : IOwnerVoiceVerifier
{
    public ConcurrentQueue<int> SpeechLengths { get; } = new();

    public OwnerVerificationResult Verify(ReadOnlySpan<float> speech16k, OwnerVoiceReference reference, CancellationToken cancellationToken)
    {
        SpeechLengths.Enqueue(speech16k.Length);
        return new OwnerVerificationResult(decision, similarity, similarity, 1, false, 1, reference.Centroid.EncoderId, TimeSpan.Zero, "scripted");
    }
}

/// <summary>
/// STUB speech recogniser: ignores the audio and returns fixed text. It exists so pipeline tests have a deterministic
/// search outcome; it is not speech recognition.
/// </summary>
public sealed class StubSpeechRecognizer(string text, double confidence = 0.9, string language = "en") : ISpeechRecognitionService
{
    public ConcurrentQueue<int> AudioLengths { get; } = new();

    public SpeechRecognizerInfo Info { get; } = new(true, "stub-recognizer", "STUB recogniser returning fixed text", 16000 * 30, "Stub");

    public Task<SpeechRecognitionResult> RecognizeAsync(ReadOnlyMemory<float> audio16k, SpeechRecognitionOptions options, CancellationToken cancellationToken)
    {
        AudioLengths.Enqueue(audio16k.Length);
        return Task.FromResult(new SpeechRecognitionResult(text, language, confidence, Math.Log(confidence), 0.01, 3,
            TimeSpan.FromSeconds(audio16k.Length / 16000.0), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, "stub-recognizer", false));
    }

    public void Dispose()
    {
    }
}

/// <summary><see cref="IVoiceModelRuntime"/> fake whose components are supplied by the test.</summary>
public sealed class FakeRuntime : IVoiceModelRuntime
{
    public Func<IVoiceActivityDetector> VadFactory { get; set; } = () => new EnergyVad();

    public Func<ITargetSpeakerExtractor> ExtractorFactory { get; set; } =
        () => new UnavailableTargetSpeakerExtractor(TargetSpeakerExtractionAvailability.NotInstalled, "No TSE model (test).");

    public List<IVoiceActivityDetector> CreatedDetectors { get; } = [];

    public int InitializeCount { get; private set; }

    public VoiceModelRuntimeStatus Status { get; set; } = VoiceModelRuntimeStatus.NotInitialized;

    public ISpeakerEmbeddingService? SpeakerEncoder { get; set; }

    public IOwnerVoiceVerifier? OwnerVerifier { get; set; }

    public string? ExtractionRequiredEncoderId { get; set; }

    public ISpeechRecognitionService Recognizer { get; set; } = new StubSpeechRecognizer("napa");

    public Task InitializeAsync(VoiceSearchSettings settings, CancellationToken cancellationToken)
    {
        InitializeCount++;
        return Task.CompletedTask;
    }

    public Task ReloadAsync(VoiceSearchSettings settings, CancellationToken cancellationToken) => InitializeAsync(settings, cancellationToken);

    public IVoiceActivityDetector CreateVoiceActivityDetector()
    {
        var vad = VadFactory();
        CreatedDetectors.Add(vad);
        return vad;
    }

    public ITargetSpeakerExtractor CreateTargetSpeakerExtractor() => ExtractorFactory();

    public TargetSpeakerExtractorStatus CreateTargetSpeakerExtractorStatusPreview() => ExtractorFactory().Status;

    public void Dispose()
    {
    }
}

/// <summary>Collects pipeline events and lets a test await a specific one without sleeping.</summary>
public sealed class PipelineProbe
{
    private readonly Lock _gate = new();
    private readonly List<PipelineEvent> _events = [];
    private readonly List<(Func<PipelineEvent, bool> Predicate, TaskCompletionSource<PipelineEvent> Done)> _waiters = [];

    public PipelineProbe(IVoiceSearchPipeline pipeline) => pipeline.EventRaised += (_, e) => Add(e.Event);

    public IReadOnlyList<PipelineEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<PipelineState> States => [.. Events.OfType<PipelineStateChangedEvent>().Select(e => e.State)];

    public Action<PipelineEvent>? OnEvent { get; set; }

    public Task<PipelineEvent> WaitForAsync(Func<PipelineEvent, bool> predicate)
    {
        lock (_gate)
        {
            var existing = _events.FirstOrDefault(predicate);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var tcs = new TaskCompletionSource<PipelineEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((predicate, tcs));
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Completes when the session has ended (Idle or Faulted after having listened).</summary>
    public Task<PipelineEvent> SessionEndedAsync() =>
        WaitForAsync(e => e is PipelineStateChangedEvent { State: PipelineState.Idle or PipelineState.Faulted }
                          && States.Contains(PipelineState.Listening));

    private void Add(PipelineEvent e)
    {
        OnEvent?.Invoke(e);
        List<TaskCompletionSource<PipelineEvent>> done = [];
        lock (_gate)
        {
            _events.Add(e);
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Predicate(e))
                {
                    done.Add(_waiters[i].Done);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var tcs in done)
        {
            tcs.TrySetResult(e);
        }
    }
}
