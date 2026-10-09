using BonikPOS.VoiceSearch.AI.Extraction;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.AI.Recognition;
using BonikPOS.VoiceSearch.AI.Recognition.Whisper;
using BonikPOS.VoiceSearch.AI.Speakers;
using BonikPOS.VoiceSearch.AI.Vad;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Core.Vad;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonikPOS.VoiceSearch.AI.Runtime;

/// <summary>
/// Loads every installed model once (persistent ONNX Runtime sessions) and exposes the components. Missing models
/// are reported as unavailable; nothing is replaced by a fake.
/// </summary>
public sealed class OnnxVoiceModelRuntime : IVoiceModelRuntime
{
    private readonly IModelProvider _models;
    private readonly IPerformanceTelemetry _telemetry;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly bool _allowTestFixtures;
    private (AcceleratorPreference, int)? _loadedWith;
    private SileroVadModel? _vad;
    private ISpeakerEmbeddingService? _encoder;
    private StreamingTseModel? _tse;
    private ISpeechRecognitionService _recognizer = new UnavailableSpeechRecognizer("Models have not been loaded yet.");
    private string _tseUnavailableMessage = "No target-speaker extraction model is installed.";
    private TargetSpeakerExtractionAvailability _tseUnavailable = TargetSpeakerExtractionAvailability.NotInstalled;

    /// <param name="allowTestFixtures">Only tests set this: lets manifests marked as test fixtures load.</param>
    public OnnxVoiceModelRuntime(
        IModelProvider models, IPerformanceTelemetry telemetry, ILogger<OnnxVoiceModelRuntime>? logger = null, bool allowTestFixtures = false)
    {
        _models = models;
        _telemetry = telemetry;
        _logger = logger ?? NullLogger<OnnxVoiceModelRuntime>.Instance;
        _allowTestFixtures = allowTestFixtures;
    }

    public VoiceModelRuntimeStatus Status { get; private set; } = VoiceModelRuntimeStatus.NotInitialized;

    public ISpeakerEmbeddingService? SpeakerEncoder => _encoder;

    public IOwnerVoiceVerifier? OwnerVerifier { get; private set; }

    public ISpeechRecognitionService Recognizer => _recognizer;

    public string? ExtractionRequiredEncoderId => _tse?.RequiredEncoderId;

    public async Task InitializeAsync(VoiceSearchSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var key = (settings.Accelerator, settings.InferenceThreads);
        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loadedWith == key)
            {
                return;
            }

            var preferences = new OnnxSessionPreferences(settings.Accelerator, settings.InferenceThreads);
            await Task.Run(() => LoadAll(preferences, cancellationToken), cancellationToken).ConfigureAwait(false);
            _loadedWith = key;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task ReloadAsync(VoiceSearchSettings settings, CancellationToken cancellationToken)
    {
        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loadedWith = null;
        }
        finally
        {
            _initGate.Release();
        }

        await InitializeAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public IVoiceActivityDetector CreateVoiceActivityDetector() =>
        _vad?.CreateDetector() ?? throw new InvalidOperationException("The voice activity model is not installed.");

    public ITargetSpeakerExtractor CreateTargetSpeakerExtractor() =>
        _tse is not null
            ? new OnnxStreamingTargetSpeakerExtractor(_tse, _logger)
            : new UnavailableTargetSpeakerExtractor(_tseUnavailable, _tseUnavailableMessage);

    public TargetSpeakerExtractorStatus CreateTargetSpeakerExtractorStatusPreview() =>
        _tse is not null
            ? new TargetSpeakerExtractorStatus(TargetSpeakerExtractionAvailability.NeedsReEnrollment, _tse.Descriptor.Id, _tse.RequiredEncoderId,
                _tse.ChunkSamples, _tse.LatencySamples, "Installed; activates when listening starts with a compatible owner profile.")
            : new TargetSpeakerExtractorStatus(_tseUnavailable, null, null, 0, 0, _tseUnavailableMessage);

    public void Dispose()
    {
        Unload();
        _initGate.Dispose();
    }

    private void LoadAll(OnnxSessionPreferences preferences, CancellationToken token)
    {
        Unload();
        var messages = new List<string>();
        var vadStatus = Load(ModelRole.VoiceActivity, model =>
        {
            _vad = SileroVadModel.Load(model, preferences, _logger);
            return new ComponentStatus(true, _vad.ModelId, "Silero VAD (streaming, 32 ms chunks)", _vad.Loaded.ExecutionProvider, _vad.Loaded.InitMilliseconds);
        }, messages, token);

        var encoderStatus = Load(ModelRole.SpeakerEncoder, model =>
        {
            var encoder = model.Descriptor.Format switch
            {
                "ge2e-onnx" => (ISpeakerEmbeddingService)Ge2eSpeakerEncoder.Load(model, preferences, _logger),
                "fbank-speaker-onnx" => FbankSpeakerEncoder.Load(model, preferences, _logger),
                _ => throw new ModelContractException($"Unknown speaker-encoder format '{model.Descriptor.Format}'."),
            };
            _encoder = encoder;
            OwnerVerifier = new OwnerVoiceVerifier(encoder);
            var (ep, ms) = encoder switch
            {
                Ge2eSpeakerEncoder g => (g.ExecutionProvider, g.InitMilliseconds),
                FbankSpeakerEncoder f => (f.ExecutionProvider, f.InitMilliseconds),
                _ => ("CPU", 0d),
            };
            return new ComponentStatus(true, encoder.Info.ModelId,
                $"Speaker encoder {encoder.Info.ModelId} ({encoder.Info.EmbeddingDimension}-dim) - owner verification", ep, ms);
        }, messages, token);

        var tseStatus = Load(ModelRole.TargetSpeakerExtraction, model =>
        {
            var tse = StreamingTseModel.Load(model, preferences, _logger);
            if (_encoder is not null && tse.RequiredEncoderId != _encoder.Info.ModelId)
            {
                string msg = $"TSE model {model.Descriptor.Id} is conditioned on {tse.RequiredEncoderId}, but the installed speaker encoder is {_encoder.Info.ModelId}.";
                tse.Dispose();
                throw new ModelContractException(msg);
            }

            _tse = tse;
            return new ComponentStatus(true, model.Descriptor.Id,
                $"Streaming target-speaker extraction ({tse.ChunkSamples} samples/chunk)", tse.Loaded.ExecutionProvider, tse.Loaded.InitMilliseconds);
        }, messages, token);
        if (!tseStatus.Available)
        {
            _tseUnavailableMessage = tseStatus.Description;
            _tseUnavailable = tseStatus.Description.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                ? TargetSpeakerExtractionAvailability.NotInstalled
                : TargetSpeakerExtractionAvailability.IncompatibleModel;
        }

        var asrStatus = Load(ModelRole.SpeechRecognition, model =>
        {
            if (model.Descriptor.Format != WhisperModelConfig.FormatId)
            {
                throw new ModelContractException($"Unknown speech-recognition format '{model.Descriptor.Format}'.");
            }

            var whisper = WhisperOnnxRecognizer.Load(model, preferences, _telemetry, _logger);
            _recognizer = whisper;
            return new ComponentStatus(true, whisper.Info.ModelId, whisper.Info.Description, whisper.EncoderExecutionProvider, whisper.InitMilliseconds);
        }, messages, token);
        if (!asrStatus.Available)
        {
            _recognizer = new UnavailableSpeechRecognizer(asrStatus.Description);
        }

        Status = new VoiceModelRuntimeStatus(true, vadStatus, encoderStatus, tseStatus, asrStatus, messages);
    }

    private ComponentStatus Load(ModelRole role, Func<ResolvedModel, ComponentStatus> load, List<string> messages, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ResolvedModel? model;
        try
        {
            model = _models.ResolveAsync(role, token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            messages.Add($"{role}: {ex.Message}");
            return ComponentStatus.Missing($"{role} model could not be resolved: {ex.Message}");
        }

        if (model is null)
        {
            return ComponentStatus.Missing($"{role} model not installed.");
        }

        if (model.Descriptor.IsTestFixture && !_allowTestFixtures)
        {
            messages.Add($"{role}: '{model.Descriptor.Id}' is a test fixture and is never loaded by the app.");
            return ComponentStatus.Missing($"{role} model not installed (only a test fixture is present).");
        }

        try
        {
            var status = load(model);
            if (status.InitMilliseconds is double ms)
            {
                _telemetry.Record(TelemetryMetrics.ModelInitPrefix + role, ms);
            }

            return status;
        }
        catch (Exception ex) when (ex is ModelContractException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or IOException or FormatException)
        {
            var state = ex is ModelContractException ? ModelState.IncompatibleContract : ModelState.LoadFailed;
            _models.ReportFailure(role, state, ex.Message);
            _logger.LogError(ex, "Loading {Role} model {Id} failed", role, model.Descriptor.Id);
            messages.Add($"{role}: {ex.Message}");
            return ComponentStatus.Missing($"{role} model '{model.Descriptor.Id}' failed to load: {ex.Message}");
        }
    }

    private void Unload()
    {
        _vad?.Dispose();
        _vad = null;
        _encoder?.Dispose();
        _encoder = null;
        OwnerVerifier = null;
        _tse?.Dispose();
        _tse = null;
        _recognizer.Dispose();
        _recognizer = new UnavailableSpeechRecognizer("Models have not been loaded yet.");
        _loadedWith = null;
    }
}
