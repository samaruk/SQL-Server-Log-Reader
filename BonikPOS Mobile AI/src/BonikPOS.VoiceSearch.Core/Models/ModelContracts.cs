using System.Text.Json;

namespace BonikPOS.VoiceSearch.Core.Models;

public enum ModelRole
{
    VoiceActivity,
    SpeakerEncoder,
    TargetSpeakerExtraction,
    SpeechRecognition,
}

public enum ModelState
{
    Ready,
    NotInstalled,
    ChecksumMismatch,
    InvalidManifest,
    IncompatibleContract,
    LoadFailed,
}

/// <summary>One entry of models/models.json.</summary>
public sealed record ModelDescriptor
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required ModelRole Role { get; init; }

    /// <summary>Adapter format, e.g. "silero-vad-v5", "ge2e-onnx", "fbank-speaker-onnx", "bonikpos-whisper-onnx/1", "streaming-tse-onnx/1".</summary>
    public required string Format { get; init; }

    /// <summary>Logical file name -> relative path.</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }

    /// <summary>Relative path -> expected lowercase SHA-256.</summary>
    public IReadOnlyDictionary<string, string> Sha256 { get; init; } = new Dictionary<string, string>();

    public string License { get; init; } = "unknown";

    public string Source { get; init; } = "unknown";

    /// <summary>True for arithmetic/random-weight graphs used by tests. Such models are never loaded by the app.</summary>
    public bool IsTestFixture { get; init; }

    /// <summary>Adapter-specific options (feature parameters, tensor names, thresholds).</summary>
    public JsonElement? Options { get; init; }
}

public sealed record ModelFileStatus(string LogicalName, string RelativePath, bool Exists, long? Bytes, string? ExpectedSha256, string? ActualSha256);

public sealed record ModelStatus(
    ModelRole Role,
    string? ModelId,
    string? Version,
    ModelState State,
    string Message,
    IReadOnlyList<ModelFileStatus> Files,
    string? License,
    string? Source);

/// <summary>A model whose files exist and passed the integrity check.</summary>
public sealed record ResolvedModel(ModelDescriptor Descriptor, IReadOnlyDictionary<string, string> FilePaths)
{
    public string PathOf(string logicalName) =>
        FilePaths.TryGetValue(logicalName, out var path)
            ? path
            : throw new KeyNotFoundException($"Model '{Descriptor.Id}' has no file '{logicalName}'.");
}

/// <summary>Locates bundled or side-loaded model files and verifies their checksums.</summary>
public interface IModelProvider
{
    Task<IReadOnlyList<ModelStatus>> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Returns the verified model for a role, or null when none is installed / verification failed.</summary>
    Task<ResolvedModel?> ResolveAsync(ModelRole role, CancellationToken cancellationToken);

    /// <summary>Lets adapters record that a model failed its tensor-contract validation or failed to load.</summary>
    void ReportFailure(ModelRole role, ModelState state, string message);
}

/// <summary>Thrown when a model's actual inputs/outputs do not match the contract its adapter requires.</summary>
public sealed class ModelContractException(string message) : InvalidOperationException(message);
