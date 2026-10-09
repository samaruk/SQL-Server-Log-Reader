using System.Threading.Channels;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Common;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonikPOS.VoiceSearch.Core.Pipeline;

public enum PipelineState
{
    Idle,
    Starting,
    Listening,
    HearingSpeech,
    Processing,
    Stopping,
    Faulted,
}

public abstract record PipelineEvent;

public sealed record PipelineStateChangedEvent(PipelineState State, string? Detail) : PipelineEvent;

/// <summary>Throttled (about 15 per second) level of the raw microphone input for the listening indicator.</summary>
public sealed record AudioLevelEvent(double RmsDbfs, double PeakDbfs, float SpeechProbability, bool InSpeech) : PipelineEvent;

public sealed record UtteranceCapturedEvent(double SpeechMilliseconds, bool Truncated) : PipelineEvent;

/// <summary>An utterance was not used, e.g. because it was attributed to someone other than the owner.</summary>
public sealed record UtteranceIgnoredEvent(OwnerVerificationResult Verification, string Reason) : PipelineEvent;

public sealed record NothingRecognizedEvent(SpeechRecognitionResult Recognition) : PipelineEvent;

public sealed record PipelineTimings(double VerificationMs, double RecognitionMs, double SearchMs, double EndOfSpeechToResultMs);

public sealed record SearchCompletedEvent(
    MedicineSearchResult Search,
    SpeechRecognitionResult Recognition,
    OwnerVerificationResult Verification,
    TargetSpeakerExtractorStatus Extraction,
    PipelineTimings Timings) : PipelineEvent;

public sealed record ListeningTimedOutEvent : PipelineEvent;

public sealed record PipelineErrorEvent(string Message, bool Fatal) : PipelineEvent;

public sealed class PipelineEventArgs(PipelineEvent pipelineEvent) : EventArgs
{
    public PipelineEvent Event { get; } = pipelineEvent;
}

public sealed record PipelineSessionOptions
{
    public required VoiceSearchSettings Settings { get; init; }

    /// <summary>Owner reference for speaker verification. Null when no compatible profile exists.</summary>
    public OwnerVoiceReference? Owner { get; init; }

    /// <summary>Owner embedding from the encoder the TSE model requires (may differ from the verification encoder).</summary>
    public SpeakerEmbedding? ExtractionTarget { get; init; }
}

public interface IVoiceSearchPipeline : IAsyncDisposable
{
    PipelineState State { get; }

    TargetSpeakerExtractorStatus? ExtractionStatus { get; }

    event EventHandler<PipelineEventArgs>? EventRaised;

    Task StartAsync(PipelineSessionOptions options, CancellationToken cancellationToken);

    /// <summary>Stops listening. An utterance in progress is finished and still recognised unless <paramref name="cancellationToken"/> fires.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Microphone -> target-speaker extraction -> VAD end-pointing -> owner verification -> ASR -> medicine search.
/// Capture and inference run on separate workers connected by bounded queues; nothing heavy runs on the native
/// audio callback or the UI thread.
/// </summary>
public sealed class VoiceSearchPipeline : IVoiceSearchPipeline
{
    private const int LevelEventEveryFrames = 3;
    private readonly IMicrophoneCapture _capture;
    private readonly IVoiceModelRuntime _runtime;
    private readonly IMedicineSearchService _search;
    private readonly IPerformanceTelemetry _telemetry;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private PipelineState _state = PipelineState.Idle;
    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private ITargetSpeakerExtractor? _extractor;
    private int _stopRequested;

    public VoiceSearchPipeline(
        IMicrophoneCapture capture,
        IVoiceModelRuntime runtime,
        IMedicineSearchService search,
        IPerformanceTelemetry telemetry,
        ILogger<VoiceSearchPipeline>? logger = null)
    {
        _capture = capture;
        _runtime = runtime;
        _search = search;
        _telemetry = telemetry;
        _logger = logger ?? NullLogger<VoiceSearchPipeline>.Instance;
    }

    public event EventHandler<PipelineEventArgs>? EventRaised;

    public PipelineState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public TargetSpeakerExtractorStatus? ExtractionStatus => _extractor?.Status;

    public async Task StartAsync(PipelineSessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        IDisposable? vad = null;
        ITargetSpeakerExtractor? extractor = null;
        CancellationTokenSource? cts = null;
        try
        {
            if (State is not (PipelineState.Idle or PipelineState.Faulted))
            {
                throw new InvalidOperationException($"Pipeline is already {State}.");
            }

            SetState(PipelineState.Starting, null);
            var settings = options.Settings;
            await _runtime.InitializeAsync(settings, cancellationToken).ConfigureAwait(false);

            var detector = _runtime.CreateVoiceActivityDetector();
            vad = detector;
            var segmenter = new UtteranceSegmenter(detector, new UtteranceSegmenterOptions
            {
                StartThreshold = settings.Pipeline.VadStartThreshold,
                EndThreshold = settings.Pipeline.VadEndThreshold,
                MinSpeechMilliseconds = settings.Pipeline.MinSpeechMilliseconds,
                EndSilenceMilliseconds = settings.Pipeline.EndSilenceMilliseconds,
                PreRollMilliseconds = settings.Pipeline.PreRollMilliseconds,
                MaxUtteranceMilliseconds = settings.Pipeline.MaxUtteranceMilliseconds,
            });

            extractor = _runtime.CreateTargetSpeakerExtractor();
            extractor.SetTarget(settings.TargetSpeakerExtractionEnabled ? options.ExtractionTarget : null);

            var queue = Channel.CreateBounded<Utterance>(
                new BoundedChannelOptions(2)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                },
                _ => _telemetry.Increment(TelemetryMetrics.RecognitionQueueDrops));

            Interlocked.Exchange(ref _stopRequested, 0);
            cts = new CancellationTokenSource();
            await _capture.StartAsync(
                new MicrophoneCaptureOptions
                {
                    FrameMilliseconds = 20,
                    QueueCapacityFrames = settings.Pipeline.QueueCapacityFrames,
                    InputPreference = settings.AudioInput,
                },
                cancellationToken).ConfigureAwait(false);

            _extractor = extractor;
            _sessionCts = cts;
            var activeExtractor = extractor;
            var token = cts.Token;
            string listening = DescribeExtraction(extractor.Status);

            // The capture worker starts only once the session is registered (task stored, state Listening). Otherwise a
            // stream that ends at once could finish the session first, leaving a stale session task behind and the
            // pipeline stuck in Listening/Stopping. The recognition worker cannot finish before the capture worker.
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var captureLoop = Task.Run(
                async () =>
                {
                    await registered.Task.ConfigureAwait(false);
                    await CaptureLoopAsync(segmenter, activeExtractor, queue.Writer, settings, token).ConfigureAwait(false);
                },
                CancellationToken.None);
            var recognitionLoop = Task.Run(() => RecognitionLoopAsync(queue.Reader, options, token), CancellationToken.None);
            _sessionTask = FinishSessionAsync(captureLoop, recognitionLoop, detector, extractor, cts);
            SetState(PipelineState.Listening, listening);
            registered.SetResult();
        }
        catch (Exception ex)
        {
            if (_sessionTask is null)
            {
                vad?.Dispose();
                extractor?.Dispose();
                cts?.Dispose();
            }

            _logger.LogError(ex, "Pipeline failed to start");
            SetState(PipelineState.Faulted, ex.Message);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? session;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = _sessionTask;
            if (session is null)
            {
                return;
            }

            RequestStop();
        }
        finally
        {
            _lifecycle.Release();
        }

        using var reg = cancellationToken.Register(() => _sessionCts?.Cancel());
        await session.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while disposing pipeline");
        }

        _lifecycle.Dispose();
    }

    private void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
        {
            return;
        }

        SetState(PipelineState.Stopping, null);
        _ = StopCaptureAsync();
    }

    private async Task StopCaptureAsync()
    {
        try
        {
            await _capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping capture failed");
            _sessionCts?.Cancel();
        }
    }

    private async Task FinishSessionAsync(
        Task captureLoop, Task recognitionLoop, IDisposable vad, ITargetSpeakerExtractor extractor, CancellationTokenSource cts)
    {
        string? fault = null;
        try
        {
            await Task.WhenAll(captureLoop, recognitionLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stop requested with cancellation.
        }
        catch (Exception ex)
        {
            fault = ex.Message;
            _logger.LogError(ex, "Pipeline session failed");
        }
        finally
        {
            try
            {
                if (_capture.State is not MicrophoneCaptureState.Stopped)
                {
                    await _capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capture stop during cleanup failed");
            }

            vad.Dispose();
            extractor.Dispose();
            cts.Dispose();
            _sessionCts = null;
            _sessionTask = null;
            SetState(fault is null ? PipelineState.Idle : PipelineState.Faulted, fault);
        }
    }

    private async Task CaptureLoopAsync(
        UtteranceSegmenter segmenter,
        ITargetSpeakerExtractor extractor,
        ChannelWriter<Utterance> output,
        VoiceSearchSettings settings,
        CancellationToken token)
    {
        var frames = _capture.Frames;
        var frame = new float[frames.FrameSamples];
        var extracted = new float[frames.FrameSamples];
        long frameCount = 0;
        long framesSinceActivity = 0;
        long maxIdleFrames = settings.Pipeline.Mode == ListeningMode.SingleQuery
            ? settings.Pipeline.MaxListenMilliseconds / 20
            : long.MaxValue;
        bool wasInSpeech = false;
        try
        {
            while (true)
            {
                var read = await frames.ReadFrameAsync(frame, token).ConfigureAwait(false);
                if (!read.HasFrame)
                {
                    break;
                }

                frameCount++;
                _telemetry.Increment(TelemetryMetrics.FramesCaptured);
                if (read.Info.Discontinuity)
                {
                    _telemetry.Increment(TelemetryMetrics.Discontinuities);
                    _telemetry.Increment(TelemetryMetrics.FramesDropped, read.Info.DroppedFramesBefore);
                    extractor.Reset();
                    segmenter.Reset();
                }

                long t0 = Ticks.Now();
                extractor.Process(frame, extracted);
                if (extractor.Status.IsActive)
                {
                    _telemetry.Record(TelemetryMetrics.TseChunkMs, Ticks.ElapsedMilliseconds(t0));
                }

                var utterance = segmenter.Push(extracted, read.Info.CaptureTimestamp);
                _telemetry.Record(TelemetryMetrics.CaptureToProcessedMs, Ticks.ElapsedMilliseconds(read.Info.CaptureTimestamp));

                if (segmenter.InSpeech != wasInSpeech)
                {
                    wasInSpeech = segmenter.InSpeech;
                    SetState(wasInSpeech ? PipelineState.HearingSpeech : PipelineState.Listening, null);
                }

                if (frameCount % LevelEventEveryFrames == 0)
                {
                    Raise(new AudioLevelEvent(
                        AudioLevel.RmsDbfs(frame), AudioLevel.ToDbfs(AudioLevel.Peak(frame)), segmenter.LastProbability, segmenter.InSpeech));
                }

                framesSinceActivity = segmenter.InSpeech ? 0 : framesSinceActivity + 1;
                if (utterance is not null)
                {
                    framesSinceActivity = 0;
                    _telemetry.Increment(TelemetryMetrics.UtterancesDetected);
                    Raise(new UtteranceCapturedEvent(utterance.SpeechMilliseconds, utterance.Truncated));
                    output.TryWrite(utterance);
                }
                else if (framesSinceActivity > maxIdleFrames && Volatile.Read(ref _stopRequested) == 0)
                {
                    Raise(new ListeningTimedOutEvent());
                    RequestStop();
                }
            }

            // Capture stopped while the owner was still speaking (tap-to-stop): finish that utterance.
            var last = segmenter.Flush();
            if (last is not null)
            {
                Raise(new UtteranceCapturedEvent(last.SpeechMilliseconds, last.Truncated));
                output.TryWrite(last);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            output.TryComplete();
        }
    }

    private async Task RecognitionLoopAsync(ChannelReader<Utterance> input, PipelineSessionOptions options, CancellationToken token)
    {
        var settings = options.Settings;
        await foreach (var utterance in input.ReadAllAsync(token).ConfigureAwait(false))
        {
            SetState(PipelineState.Processing, null);
            try
            {
                bool done = await ProcessUtteranceAsync(utterance, options, settings, token).ConfigureAwait(false);
                if (done && settings.Pipeline.Mode == ListeningMode.SingleQuery)
                {
                    RequestStop();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (SpeechRecognitionUnavailableException ex)
            {
                Raise(new PipelineErrorEvent(ex.Message, Fatal: true));
                RequestStop();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utterance processing failed");
                Raise(new PipelineErrorEvent(ex.Message, Fatal: false));
                if (settings.Pipeline.Mode == ListeningMode.SingleQuery)
                {
                    RequestStop();
                }
            }

            if (Volatile.Read(ref _stopRequested) == 0)
            {
                SetState(PipelineState.Listening, null);
            }
        }
    }

    /// <returns>True when the utterance produced a search result.</returns>
    private async Task<bool> ProcessUtteranceAsync(
        Utterance utterance, PipelineSessionOptions options, VoiceSearchSettings settings, CancellationToken token)
    {
        long t0 = Ticks.Now();
        var verification = Verify(utterance, options.Owner, token);
        double verifyMs = Ticks.ElapsedMilliseconds(t0);
        _telemetry.Record(TelemetryMetrics.SpeakerVerifyMs, verifyMs);

        switch (verification.Decision)
        {
            case OwnerVoiceDecision.NotOwner when settings.Verification.RequireOwnerMatch:
                _telemetry.Increment(TelemetryMetrics.UtterancesNotOwner);
                Raise(new UtteranceIgnoredEvent(verification, "The voice did not match the enrolled owner."));
                return false;
            case OwnerVoiceDecision.InsufficientSpeech:
                Raise(new UtteranceIgnoredEvent(verification, "Too little speech to check the speaker."));
                return false;
            case OwnerVoiceDecision.Uncertain:
                _telemetry.Increment(TelemetryMetrics.UtterancesUncertain);
                break;
        }

        var recognizer = _runtime.Recognizer;
        if (!recognizer.Info.IsAvailable)
        {
            throw new SpeechRecognitionUnavailableException(recognizer.Info.StatusMessage);
        }

        long t1 = Ticks.Now();
        var recognition = await recognizer.RecognizeAsync(
            utterance.Audio, new SpeechRecognitionOptions { LanguageMode = settings.LanguageMode }, token).ConfigureAwait(false);
        double recognitionMs = Ticks.ElapsedMilliseconds(t1);
        _telemetry.Record(TelemetryMetrics.AsrTotalMs, recognitionMs);

        if (string.IsNullOrWhiteSpace(recognition.Text))
        {
            Raise(new NothingRecognizedEvent(recognition));
            return false;
        }

        long t2 = Ticks.Now();
        var search = await _search.SearchAsync(
            new MedicineQuery(recognition.Text, QuerySource.Voice, recognition.Confidence, verification.Decision)
            {
                Alternatives = [.. recognition.Alternatives.Select(a => a.Text)],
            }, token)
            .ConfigureAwait(false);
        double searchMs = Ticks.ElapsedMilliseconds(t2);
        double endToResult = Ticks.ElapsedMilliseconds(utterance.EndDetectedTimestamp);
        _telemetry.Record(TelemetryMetrics.SearchMs, searchMs);
        _telemetry.Record(TelemetryMetrics.EndOfSpeechToResultMs, endToResult);

        Raise(new SearchCompletedEvent(
            search,
            recognition,
            verification,
            _extractor?.Status ?? new TargetSpeakerExtractorStatus(TargetSpeakerExtractionAvailability.NotInstalled, null, null, 0, 0, "No extractor"),
            new PipelineTimings(verifyMs, recognitionMs, searchMs, endToResult)));
        return true;
    }

    private OwnerVerificationResult Verify(Utterance utterance, OwnerVoiceReference? owner, CancellationToken token)
    {
        if (owner is null)
        {
            return OwnerVerificationResult.Unavailable(OwnerVoiceDecision.NoProfile, "No compatible owner voice profile is enrolled.");
        }

        var verifier = _runtime.OwnerVerifier;
        if (verifier is null)
        {
            return OwnerVerificationResult.Unavailable(OwnerVoiceDecision.ModelUnavailable, "The speaker-encoder model is not installed.");
        }

        return verifier.Verify(utterance.SpeechOnly, owner, token);
    }

    private static string DescribeExtraction(TargetSpeakerExtractorStatus status) =>
        status.IsActive
            ? $"Target-speaker extraction active ({status.ModelId})."
            : $"Target-speaker extraction unavailable: {status.Message}";

    private void SetState(PipelineState state, string? detail)
    {
        lock (_gate)
        {
            // A stop requested just as the session ended on its own must not move the finished pipeline back to Stopping.
            if (_state == state || (state == PipelineState.Stopping && _state is PipelineState.Idle or PipelineState.Faulted))
            {
                return;
            }

            _state = state;
        }

        Raise(new PipelineStateChangedEvent(state, detail));
    }

    private void Raise(PipelineEvent e)
    {
        try
        {
            EventRaised?.Invoke(this, new PipelineEventArgs(e));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A pipeline event handler threw");
        }
    }
}
