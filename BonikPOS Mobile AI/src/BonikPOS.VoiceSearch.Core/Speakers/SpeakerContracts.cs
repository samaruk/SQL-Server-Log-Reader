namespace BonikPOS.VoiceSearch.Core.Speakers;

/// <summary>Describes the speaker encoder model that produced an embedding.</summary>
public sealed record SpeakerEncoderInfo(
    string ModelId,
    string ModelVersion,
    string ModelSha256,
    int EmbeddingDimension,
    int SampleRate,
    double WindowSeconds,
    double MinimumSpeechSeconds)
{
    /// <summary>Provisional, encoder-specific similarity thresholds (from the model manifest). Calibrate them with
    /// the evaluation plan in docs/EVALUATION_PLAN.md before relying on them.</summary>
    public SpeakerCalibration Calibration { get; init; } = new();
}

/// <summary>Cosine-similarity limits for one encoder. Values are not universal: every encoder has its own scale.</summary>
public sealed record SpeakerCalibration
{
    public double DefaultAcceptThreshold { get; init; } = 0.75;

    public double MinAcceptThreshold { get; init; } = 0.65;

    public double MaxAcceptThreshold { get; init; } = 0.88;

    /// <summary>Minimum similarity of a new enrollment sample to the other samples' centroid.</summary>
    public double EnrollmentMinConsistency { get; init; } = 0.70;

    /// <summary>Minimum similarity of every window of a sample to the sample itself (single-speaker check).</summary>
    public double SingleSpeakerMinWindow { get; init; } = 0.70;
}

/// <summary>An L2-normalised speaker embedding (d-vector / x-vector) tagged with the encoder that produced it.</summary>
public sealed class SpeakerEmbedding
{
    public SpeakerEmbedding(string encoderId, string encoderVersion, float[] vector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoderId);
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length == 0)
        {
            throw new ArgumentException("Embedding vector is empty.", nameof(vector));
        }

        EncoderId = encoderId;
        EncoderVersion = encoderVersion;
        Vector = Normalize(vector);
    }

    public string EncoderId { get; }

    public string EncoderVersion { get; }

    /// <summary>Unit-length vector. Do not mutate.</summary>
    public float[] Vector { get; }

    public int Dimension => Vector.Length;

    /// <summary>Cosine similarity of two embeddings from the same encoder.</summary>
    public double CosineSimilarity(SpeakerEmbedding other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(EncoderId, other.EncoderId, StringComparison.Ordinal) || Dimension != other.Dimension)
        {
            throw new InvalidOperationException(
                $"Cannot compare embeddings from different encoders ({EncoderId} vs {other.EncoderId}).");
        }

        return Dot(Vector, other.Vector);
    }

    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vectors have different lengths.");
        }

        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Returns a unit-length copy. Throws for all-zero or non-finite vectors.</summary>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        double sq = 0;
        foreach (float v in vector)
        {
            if (!float.IsFinite(v))
            {
                throw new ArgumentException("Embedding contains NaN or infinity.");
            }

            sq += (double)v * v;
        }

        if (sq <= 1e-20)
        {
            throw new ArgumentException("Embedding has zero length.");
        }

        double inv = 1.0 / Math.Sqrt(sq);
        var result = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] * inv);
        }

        return result;
    }

    /// <summary>Centroid of embeddings from one encoder: the normalised mean of unit vectors.</summary>
    public static SpeakerEmbedding Centroid(IReadOnlyList<SpeakerEmbedding> embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        if (embeddings.Count == 0)
        {
            throw new ArgumentException("At least one embedding is required.", nameof(embeddings));
        }

        var first = embeddings[0];
        var sum = new float[first.Dimension];
        foreach (var e in embeddings)
        {
            if (e.EncoderId != first.EncoderId || e.Dimension != first.Dimension)
            {
                throw new InvalidOperationException("All embeddings must come from the same encoder.");
            }

            for (int i = 0; i < sum.Length; i++)
            {
                sum[i] += e.Vector[i];
            }
        }

        return new SpeakerEmbedding(first.EncoderId, first.EncoderVersion, sum);
    }
}

/// <summary>Embedding of one analysis window inside an utterance.</summary>
public readonly record struct WindowEmbedding(int StartSample, int LengthSamples, SpeakerEmbedding Embedding);

/// <summary>Computes speaker embeddings with a compatible speaker-encoder model (CPU bound; call off the UI thread).</summary>
public interface ISpeakerEmbeddingService : IDisposable
{
    SpeakerEncoderInfo Info { get; }

    /// <summary>Embedding of a whole utterance (16 kHz mono, speech only).</summary>
    SpeakerEmbedding EmbedUtterance(ReadOnlySpan<float> samples, CancellationToken cancellationToken = default);

    /// <summary>Embeddings of overlapping windows; used to detect a second voice inside one utterance.</summary>
    IReadOnlyList<WindowEmbedding> EmbedWindows(
        ReadOnlySpan<float> samples, double windowSeconds, double hopSeconds, CancellationToken cancellationToken = default);
}

public enum OwnerVoiceDecision
{
    /// <summary>The speech matches the enrolled owner closely enough.</summary>
    Owner,
    /// <summary>The speech does not match the enrolled owner.</summary>
    NotOwner,
    /// <summary>Borderline similarity, or a second voice was detected inside the utterance.</summary>
    Uncertain,
    /// <summary>Too little speech to make a decision.</summary>
    InsufficientSpeech,
    /// <summary>No compatible owner profile is enrolled.</summary>
    NoProfile,
    /// <summary>The speaker encoder model is not available.</summary>
    ModelUnavailable,
}

/// <summary>Thresholds used to compare an utterance with the owner's enrolled voice.</summary>
/// <param name="AcceptThreshold">Cosine similarity at or above which the utterance is attributed to the owner.</param>
/// <param name="RejectThreshold">Below this the utterance is attributed to someone else.</param>
/// <param name="WindowFloor">If any analysis window falls below this while the utterance as a whole passes, a second voice is suspected.</param>
public sealed record OwnerVoiceReference(SpeakerEmbedding Centroid, double AcceptThreshold, double RejectThreshold, double WindowFloor);

public sealed record OwnerVerificationResult(
    OwnerVoiceDecision Decision,
    double Similarity,
    double MinWindowSimilarity,
    double OwnerWindowFraction,
    bool OtherVoiceSuspected,
    int Windows,
    string? EncoderId,
    TimeSpan Elapsed,
    string Reason)
{
    public static OwnerVerificationResult Unavailable(OwnerVoiceDecision decision, string reason) =>
        new(decision, double.NaN, double.NaN, double.NaN, false, 0, null, TimeSpan.Zero, reason);
}

/// <summary>
/// Speaker VERIFICATION of a finished utterance against the enrolled owner. This is not target-speaker extraction:
/// it can reject utterances spoken by other people but it cannot remove another voice that overlaps the owner's.
/// </summary>
public interface IOwnerVoiceVerifier
{
    OwnerVerificationResult Verify(ReadOnlySpan<float> speech16k, OwnerVoiceReference reference, CancellationToken cancellationToken);
}
