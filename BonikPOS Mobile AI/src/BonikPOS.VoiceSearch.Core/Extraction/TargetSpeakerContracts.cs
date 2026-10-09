using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Core.Extraction;

public enum TargetSpeakerExtractionAvailability
{
    /// <summary>A real target-speaker extraction model is loaded and conditioned on the owner's embedding.</summary>
    Active,
    /// <summary>No compatible extraction model is installed. Audio passes through unchanged.</summary>
    NotInstalled,
    /// <summary>A model is installed but the owner profile has no embedding from the encoder the model requires.</summary>
    NeedsReEnrollment,
    /// <summary>The installed model does not match its declared tensor contract.</summary>
    IncompatibleModel,
    /// <summary>The model failed at runtime.</summary>
    Failed,
    /// <summary>Disabled in settings.</summary>
    Disabled,
}

public sealed record TargetSpeakerExtractorStatus(
    TargetSpeakerExtractionAvailability Availability,
    string? ModelId,
    string? RequiredEncoderId,
    int ChunkSamples,
    int AlgorithmicLatencySamples,
    string Message)
{
    public bool IsActive => Availability == TargetSpeakerExtractionAvailability.Active;
}

/// <summary>
/// Streaming target-speaker extraction (TSE): suppresses every voice except the enrolled target speaker, including
/// simultaneous speech, as far as the model permits. Distinct from noise suppression (removes non-speech noise) and
/// from speaker identification/verification (decides who spoke, but removes nothing).
/// </summary>
/// <remarks>
/// <see cref="Process"/> is called from the pipeline worker thread, never from the microphone callback. When no
/// model is active, implementations copy input to output unchanged and report a non-active status; callers must
/// never present that pass-through as extraction.
/// </remarks>
public interface ITargetSpeakerExtractor : IDisposable
{
    TargetSpeakerExtractorStatus Status { get; }

    /// <summary>Sets (or clears) the target speaker. Embeddings from an incompatible encoder are rejected.</summary>
    void SetTarget(SpeakerEmbedding? target);

    /// <summary>
    /// Streams <paramref name="input"/> through the extractor and writes exactly <c>input.Length</c> samples to
    /// <paramref name="output"/> (delayed by <see cref="TargetSpeakerExtractorStatus.AlgorithmicLatencySamples"/>).
    /// Model state is carried across calls.
    /// </summary>
    void Process(ReadOnlySpan<float> input, Span<float> output);

    /// <summary>Clears streaming state (call after a capture discontinuity or between sessions).</summary>
    void Reset();
}
