using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>Enrollment results and the explicit decision to save the encrypted profile.</summary>
public sealed partial class EnrollmentResultViewModel(
    OwnerProfileService profiles, SessionState session, INavigationService navigation) : ViewModelBase
{
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConsistencyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModelText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasDraft { get; set; }

    [RelayCommand]
    private void Appearing()
    {
        var draft = session.EnrollmentDraft;
        HasDraft = draft is not null;
        if (draft is null)
        {
            Summary = "There is no enrollment to save.";
            return;
        }

        var e = draft.Embeddings[0];
        Summary = $"{draft.Enrollment.AcceptedSamples} samples accepted, {draft.Enrollment.RejectedSamples} re-recorded, " +
                  $"{draft.Enrollment.TotalSpeechSeconds:0} s of speech. Languages: {string.Join(", ", draft.Enrollment.Languages)}.";
        ConsistencyText = $"Your samples agree with each other: average {e.MeanSampleSimilarity:0.00}, lowest {e.MinSampleSimilarity:0.00}. " +
                          $"Owner-match threshold set to {e.SuggestedAcceptThreshold:0.00} (adjustable in Settings > Owner voice).";
        ModelText = $"Speaker model {e.EncoderId} {e.EncoderVersion}, {e.Centroid.Length}-dimensional embedding. Stored encrypted on this phone only.";
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        if (session.EnrollmentDraft is not { } draft)
        {
            return;
        }

        await profiles.SaveAsync(draft, CancellationToken.None);
        session.EnrollmentDraft = null;
        session.EnrollmentSamples = [];
        await navigation.GoToAsync(Routes.Search);
    }, "Could not save the voice profile");

    [RelayCommand]
    private Task DiscardAsync()
    {
        session.EnrollmentDraft = null;
        session.EnrollmentSamples = [];
        return navigation.GoToAsync(Routes.Search);
    }
}
