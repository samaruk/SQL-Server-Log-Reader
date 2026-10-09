using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Core.History;

public enum HistoryOutcome
{
    Confirmed,
    Corrected,
    NoMatch,
    Dismissed,
}

/// <summary>One search the owner made. Contains no audio.</summary>
public sealed record SearchHistoryEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string QueryText,
    QuerySource Source,
    SearchDecision Decision,
    HistoryOutcome Outcome,
    string? SelectedProductId,
    string? SelectedProductLabel,
    double? RecognitionConfidence,
    OwnerVoiceDecision? SpeakerDecision);

public interface ISearchHistoryStore
{
    Task AddAsync(SearchHistoryEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<SearchHistoryEntry>> GetRecentAsync(int maxEntries, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
