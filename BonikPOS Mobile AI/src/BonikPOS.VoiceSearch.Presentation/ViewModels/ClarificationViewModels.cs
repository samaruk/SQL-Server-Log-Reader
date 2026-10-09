using System.Collections.ObjectModel;
using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.MedicineSearch.Ranking;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>"Which one did you mean?" - shown when several products fit. The owner must choose explicitly.</summary>
public sealed partial class AmbiguityViewModel(
    SessionState session, ISearchHistoryStore history, SettingsService settings, INavigationService navigation, TimeProvider time) : ViewModelBase
{
    public ObservableCollection<ResultItemViewModel> Options { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Explanation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Understood { get; set; } = string.Empty;

    [RelayCommand]
    private void Appearing()
    {
        Options.Clear();
        var result = session.PendingResult;
        if (result is null)
        {
            return;
        }

        Query = result.Query.Text;
        Explanation = result.Explanation;
        Understood = Interpretation.Describe(result.Parsed);
        int rank = 1;
        foreach (var c in result.Candidates.Where(c => result.Clarifications.Any(o => o.ProductId == c.Product.ProductId)))
        {
            Options.Add(new ResultItemViewModel(c, rank++));
        }
    }

    [RelayCommand]
    private Task ChooseAsync(ResultItemViewModel? option) => RunAsync(async () =>
    {
        if (option is null || session.PendingResult is not { } result)
        {
            return;
        }

        var p = option.Candidate.Product;
        if ((await settings.LoadAsync(CancellationToken.None)).HistoryEnabled)
        {
            await history.AddAsync(new SearchHistoryEntry(Guid.NewGuid(), time.GetUtcNow(), result.Query.Text, result.Query.Source,
                result.Decision, result.Query.Source == QuerySource.Correction ? HistoryOutcome.Corrected : HistoryOutcome.Confirmed,
                p.ProductId, SearchDecisionPolicy.Label(p), result.Query.RecognitionConfidence, result.Query.SpeakerDecision), CancellationToken.None);
        }

        session.LastSelectionMessage = $"Selected {SearchDecisionPolicy.Label(p)} (ID {p.ProductId}).";
        session.ClearPending();
        await navigation.GoBackAsync();
    }, "Could not record the choice");

    [RelayCommand]
    private Task NoneOfTheseAsync()
    {
        session.TextToEdit = session.PendingResult?.Query.Text;
        session.ClearPending();
        return navigation.GoBackAsync();
    }
}

/// <summary>No product matched: explain what was understood and offer to correct or repeat.</summary>
public sealed partial class NoMatchViewModel(SessionState session, INavigationService navigation) : ViewModelBase
{
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Understood { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Explanation { get; set; } = string.Empty;

    public IReadOnlyList<string> Tips { get; } =
    [
        "Say the brand name clearly, then the strength, for example \"Seclo twenty\".",
        "If the name was misheard, edit the text and search again.",
        "Check that the product exists in the connected catalog.",
    ];

    /// <summary>Similar catalog names. They are not matches; picking one only starts a new search.</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    [ObservableProperty]
    public partial bool HasSuggestions { get; set; }

    [RelayCommand]
    private void Appearing()
    {
        var result = session.PendingResult;
        Query = result?.Query.Text ?? string.Empty;
        Understood = result is null ? string.Empty : Interpretation.Describe(result.Parsed);
        Explanation = result?.Explanation ?? string.Empty;
        Suggestions.Clear();
        foreach (var s in result?.Suggestions ?? [])
        {
            Suggestions.Add(s.QueryText);
        }

        HasSuggestions = Suggestions.Count > 0;
    }

    [RelayCommand]
    private Task SearchSuggestionAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        session.TextToEdit = text;
        session.SearchTextOnReturn = true;
        session.ClearPending();
        return navigation.GoBackAsync();
    }

    [RelayCommand]
    private Task EditTextAsync()
    {
        session.TextToEdit = Query;
        session.ClearPending();
        return navigation.GoBackAsync();
    }

    [RelayCommand]
    private Task TryAgainAsync()
    {
        session.ClearPending();
        return navigation.GoBackAsync();
    }
}

/// <summary>Human-readable summary of what the query parser understood.</summary>
public static class Interpretation
{
    public static string Describe(ParsedMedicineQuery parsed)
    {
        var parts = new List<string> { $"Name: \"{(parsed.NameText.Length > 0 ? parsed.NameText : "(none)")}\"" };
        if (parsed.Strengths.Count > 0)
        {
            parts.Add("strength: " + string.Join(" + ", parsed.Strengths));
        }
        else if (parsed.BareNumbers.Count > 0)
        {
            parts.Add("number: " + string.Join(", ", parsed.BareNumbers));
        }

        if (parsed.Forms.Count > 0)
        {
            parts.Add("form: " + string.Join(", ", parsed.Forms));
        }

        return string.Join("; ", parts);
    }
}
