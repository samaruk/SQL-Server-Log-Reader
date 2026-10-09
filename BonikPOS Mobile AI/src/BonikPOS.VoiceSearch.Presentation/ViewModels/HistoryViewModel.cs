using System.Collections.ObjectModel;
using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

public sealed record HistoryItem(string When, string Query, string Outcome, string? Product, string Details);

/// <summary>Search history (stored encrypted on the device; no audio).</summary>
public sealed partial class HistoryViewModel(ISearchHistoryStore history, SettingsService settings, IDialogService dialogs) : ViewModelBase
{
    public ObservableCollection<HistoryItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool HistoryEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [RelayCommand]
    private Task AppearingAsync() => RunAsync(async () =>
    {
        HistoryEnabled = (await settings.LoadAsync(CancellationToken.None)).HistoryEnabled;
        Items.Clear();
        foreach (var e in await history.GetRecentAsync(200, CancellationToken.None))
        {
            string outcome = e.Outcome switch
            {
                HistoryOutcome.Confirmed => "Selected",
                HistoryOutcome.Corrected => "Selected after correction",
                HistoryOutcome.Dismissed => "Dismissed",
                _ => "No match",
            };
            string details = $"{e.Source}" + (e.RecognitionConfidence is double c ? $" · recognition {c:P0}" : string.Empty)
                             + (e.SpeakerDecision is { } d ? $" · voice {d}" : string.Empty);
            Items.Add(new HistoryItem(e.Timestamp.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                e.QueryText, outcome, e.SelectedProductLabel is null ? null : $"{e.SelectedProductLabel} (ID {e.SelectedProductId})", details));
        }

        IsEmpty = Items.Count == 0;
    }, "Could not load history");

    [RelayCommand]
    private Task ClearAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync("Clear history", "Delete all search history from this phone?", "Delete", "Cancel"))
        {
            return;
        }

        await history.ClearAsync(CancellationToken.None);
        Items.Clear();
        IsEmpty = true;
    }, "Could not clear history");

    partial void OnHistoryEnabledChanged(bool value) =>
        _ = settings.UpdateAsync(s => s with { HistoryEnabled = value }, CancellationToken.None);
}
