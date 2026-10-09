using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.AI.Extraction;

/// <summary>
/// Used when no compatible target-speaker extraction model is installed (or it is disabled / failed). It copies audio
/// unchanged and its status says so: it is NOT target-speaker extraction and must never be presented as such.
/// </summary>
public sealed class UnavailableTargetSpeakerExtractor(TargetSpeakerExtractionAvailability availability, string message) : ITargetSpeakerExtractor
{
    public TargetSpeakerExtractorStatus Status { get; } = new(availability, null, null, 0, 0, message);

    public void SetTarget(SpeakerEmbedding? target)
    {
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output) => input.CopyTo(output);

    public void Reset()
    {
    }

    public void Dispose()
    {
    }
}
