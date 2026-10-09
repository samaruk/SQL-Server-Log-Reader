using System.Diagnostics;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.AI.Speakers;

/// <summary>
/// Compares a finished utterance with the enrolled owner (cosine similarity of speaker embeddings) and looks for a
/// second voice by embedding overlapping windows. This is speaker verification - the fallback used while no
/// target-speaker extraction model is installed. It cannot separate overlapping voices; when it suspects one, the
/// result is reported as <see cref="OwnerVoiceDecision.Uncertain"/>.
/// </summary>
public sealed class OwnerVoiceVerifier(ISpeakerEmbeddingService encoder) : IOwnerVoiceVerifier
{
    /// <summary>Window analysis hop as a fraction of the encoder window.</summary>
    public const double WindowHopFraction = 0.5;

    public OwnerVerificationResult Verify(ReadOnlySpan<float> speech16k, OwnerVoiceReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var sw = Stopwatch.StartNew();
        var info = encoder.Info;
        if (reference.Centroid.EncoderId != info.ModelId || reference.Centroid.Dimension != info.EmbeddingDimension)
        {
            return OwnerVerificationResult.Unavailable(OwnerVoiceDecision.NoProfile,
                "The enrolled profile was made with a different speaker model; re-enrollment is required.");
        }

        double seconds = (double)speech16k.Length / info.SampleRate;
        if (seconds < info.MinimumSpeechSeconds)
        {
            return new OwnerVerificationResult(OwnerVoiceDecision.InsufficientSpeech, double.NaN, double.NaN, double.NaN,
                false, 0, info.ModelId, sw.Elapsed, $"Only {seconds:0.0} s of speech; at least {info.MinimumSpeechSeconds:0.0} s is needed.");
        }

        var utterance = encoder.EmbedUtterance(speech16k, cancellationToken);
        double similarity = utterance.CosineSimilarity(reference.Centroid);

        double minWindow = similarity;
        double ownerFraction = similarity >= reference.RejectThreshold ? 1 : 0;
        int windows = 1;
        if (seconds >= info.WindowSeconds * 1.5)
        {
            var parts = encoder.EmbedWindows(speech16k, info.WindowSeconds, info.WindowSeconds * WindowHopFraction, cancellationToken);
            windows = parts.Count;
            int owner = 0;
            minWindow = double.MaxValue;
            foreach (var part in parts)
            {
                double s = part.Embedding.CosineSimilarity(reference.Centroid);
                minWindow = Math.Min(minWindow, s);
                if (s >= reference.RejectThreshold)
                {
                    owner++;
                }
            }

            ownerFraction = (double)owner / Math.Max(1, parts.Count);
        }

        bool otherVoice = windows > 1 && minWindow < reference.WindowFloor;
        OwnerVoiceDecision decision;
        string reason;
        if (similarity >= reference.AcceptThreshold)
        {
            decision = otherVoice ? OwnerVoiceDecision.Uncertain : OwnerVoiceDecision.Owner;
            reason = otherVoice
                ? $"Matches the owner overall ({similarity:0.00}) but part of the audio sounds like another voice ({minWindow:0.00})."
                : $"Matches the enrolled owner ({similarity:0.00} >= {reference.AcceptThreshold:0.00}).";
        }
        else if (similarity >= reference.RejectThreshold)
        {
            decision = OwnerVoiceDecision.Uncertain;
            reason = $"Borderline match with the owner ({similarity:0.00}).";
        }
        else
        {
            decision = OwnerVoiceDecision.NotOwner;
            reason = $"Does not match the enrolled owner ({similarity:0.00} < {reference.RejectThreshold:0.00}).";
        }

        return new OwnerVerificationResult(decision, similarity, minWindow, ownerFraction, otherVoice, windows, info.ModelId, sw.Elapsed, reason);
    }
}
