using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Core.Profiles;

/// <summary>
/// The enrolled owner's voice profile. Contains biometric-derived data (speaker embeddings) and is stored encrypted.
/// Raw enrollment audio is never part of the profile.
/// </summary>
/// <remarks>A voice profile is not a password and not proof of identity. It is used only to prefer the owner's voice.</remarks>
public sealed record VoiceProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid ProfileId { get; init; } = Guid.NewGuid();

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public string? DisplayName { get; init; }

    public required ConsentRecord Consent { get; init; }

    /// <summary>One entry per speaker-encoder model that the enrollment produced embeddings for.</summary>
    public required IReadOnlyList<EnrolledEmbedding> Embeddings { get; init; }

    public required EnrollmentSummary Enrollment { get; init; }

    public EnrolledEmbedding? FindEmbedding(string encoderId) =>
        Embeddings.FirstOrDefault(e => string.Equals(e.EncoderId, encoderId, StringComparison.Ordinal));
}

/// <param name="ConsentTextVersion">Version of the consent wording the owner accepted (see docs/PRIVACY.md).</param>
public sealed record ConsentRecord(DateTimeOffset AcceptedUtc, string ConsentTextVersion);

/// <summary>Owner embedding for one encoder model, with the calibration statistics measured during enrollment.</summary>
public sealed record EnrolledEmbedding(
    string EncoderId,
    string EncoderVersion,
    string EncoderSha256,
    float[] Centroid,
    int SampleCount,
    double MeanSampleSimilarity,
    double MinSampleSimilarity,
    double SuggestedAcceptThreshold)
{
    public SpeakerEmbedding ToEmbedding() => new(EncoderId, EncoderVersion, Centroid);
}

public sealed record EnrollmentSummary(
    int AcceptedSamples,
    int RejectedSamples,
    double TotalSpeechSeconds,
    IReadOnlyList<string> Languages,
    string AppVersion);

public enum ProfileCompatibility
{
    Compatible,
    /// <summary>The profile was enrolled with a different speaker-encoder model (or version/checksum).</summary>
    EncoderChanged,
    /// <summary>The profile was written by a newer app version.</summary>
    SchemaTooNew,
    NoProfile,
}

public static class VoiceProfileCompatibility
{
    /// <summary>
    /// Decides whether <paramref name="profile"/> can be used with the installed encoder. Embeddings from different
    /// models live in different vector spaces, so a model change always requires re-enrollment.
    /// </summary>
    public static ProfileCompatibility Check(VoiceProfile? profile, SpeakerEncoderInfo encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        if (profile is null)
        {
            return ProfileCompatibility.NoProfile;
        }

        if (profile.SchemaVersion > VoiceProfile.CurrentSchemaVersion)
        {
            return ProfileCompatibility.SchemaTooNew;
        }

        var e = profile.FindEmbedding(encoder.ModelId);
        if (e is null
            || !string.Equals(e.EncoderVersion, encoder.ModelVersion, StringComparison.Ordinal)
            || !string.Equals(e.EncoderSha256, encoder.ModelSha256, StringComparison.OrdinalIgnoreCase)
            || e.Centroid.Length != encoder.EmbeddingDimension)
        {
            return ProfileCompatibility.EncoderChanged;
        }

        return ProfileCompatibility.Compatible;
    }
}

/// <summary>Secure persistence of the owner voice profile.</summary>
public interface IVoiceProfileStore
{
    Task<bool> ExistsAsync(CancellationToken cancellationToken);

    /// <summary>Returns null when no profile exists. Throws <see cref="VoiceProfileCorruptException"/> on tampering.</summary>
    Task<VoiceProfile?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(VoiceProfile profile, CancellationToken cancellationToken);

    /// <summary>Deletes the profile and its encryption key (crypto-shredding).</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}

public sealed class VoiceProfileCorruptException(string message, Exception? inner = null) : Exception(message, inner);
