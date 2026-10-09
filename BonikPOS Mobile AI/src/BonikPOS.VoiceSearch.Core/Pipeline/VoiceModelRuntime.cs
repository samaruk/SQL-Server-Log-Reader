using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Core.Vad;

namespace BonikPOS.VoiceSearch.Core.Pipeline;

/// <summary>Availability of one AI component, shown on the diagnostics screen.</summary>
public sealed record ComponentStatus(
    bool Available, string? ModelId, string Description, string? ExecutionProvider, double? InitMilliseconds)
{
    public static ComponentStatus Missing(string description) => new(false, null, description, null, null);
}

public sealed record VoiceModelRuntimeStatus(
    bool IsInitialized,
    ComponentStatus VoiceActivity,
    ComponentStatus SpeakerEncoder,
    ComponentStatus TargetSpeakerExtraction,
    ComponentStatus SpeechRecognition,
    IReadOnlyList<string> Messages)
{
    public static VoiceModelRuntimeStatus NotInitialized { get; } = new(
        false,
        ComponentStatus.Missing("Not loaded"),
        ComponentStatus.Missing("Not loaded"),
        ComponentStatus.Missing("Not loaded"),
        ComponentStatus.Missing("Not loaded"),
        []);
}

/// <summary>
/// Owns the loaded models (persistent ONNX Runtime sessions) and hands out per-stream component instances.
/// Components whose model is missing are reported as unavailable instead of being replaced by fakes.
/// </summary>
public interface IVoiceModelRuntime : IDisposable
{
    VoiceModelRuntimeStatus Status { get; }

    /// <summary>Loads (or reloads after a settings change) every installed model. Safe to call repeatedly.</summary>
    Task InitializeAsync(VoiceSearchSettings settings, CancellationToken cancellationToken);

    /// <summary>Unloads and loads all models again (after installing or removing a model).</summary>
    Task ReloadAsync(VoiceSearchSettings settings, CancellationToken cancellationToken);

    /// <summary>New stateful VAD instance sharing the loaded session. Throws if no VAD model is available.</summary>
    IVoiceActivityDetector CreateVoiceActivityDetector();

    /// <summary>Null when no speaker-encoder model is installed.</summary>
    ISpeakerEmbeddingService? SpeakerEncoder { get; }

    /// <summary>Null when no speaker-encoder model is installed.</summary>
    IOwnerVoiceVerifier? OwnerVerifier { get; }

    /// <summary>New streaming extractor. Returns a non-active pass-through extractor when no TSE model is installed.</summary>
    ITargetSpeakerExtractor CreateTargetSpeakerExtractor();

    /// <summary>Status the next extractor would report, without creating one (for UI indicators).</summary>
    TargetSpeakerExtractorStatus CreateTargetSpeakerExtractorStatusPreview();

    /// <summary>Encoder id the installed TSE model must be conditioned with, or null when no TSE model is installed.</summary>
    string? ExtractionRequiredEncoderId { get; }

    /// <summary>Always non-null; reports <see cref="SpeechRecognizerInfo.IsAvailable"/> = false when no ASR model is installed.</summary>
    ISpeechRecognitionService Recognizer { get; }
}
