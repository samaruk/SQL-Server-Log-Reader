using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>Owner voice profile management: status, thresholds, re-enrollment and deletion.</summary>
public sealed partial class VoiceSettingsViewModel(
    OwnerProfileService profiles,
    IVoiceModelRuntime runtime,
    SettingsService settings,
    INavigationService navigation,
    IDialogService dialogs) : ViewModelBase
{
    [ObservableProperty]
    public partial bool HasProfile { get; set; }

    [ObservableProperty]
    public partial string ProfileStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileDetails { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RequireOwnerMatch { get; set; } = true;

    [ObservableProperty]
    public partial bool UseCustomThreshold { get; set; }

    [ObservableProperty]
    public partial double Threshold { get; set; } = 0.75;

    [ObservableProperty]
    public partial double SuggestedThreshold { get; set; }

    [ObservableProperty]
    public partial bool ExtractionEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string ExtractionStatus { get; set; } = string.Empty;

    [RelayCommand]
    private Task AppearingAsync() => RunAsync(async () =>
    {
        var s = await settings.LoadAsync(CancellationToken.None);
        await runtime.InitializeAsync(s, CancellationToken.None);
        var state = await profiles.GetStateAsync(CancellationToken.None);
        HasProfile = state.Profile is not null;
        ProfileStatus = state.Message;
        if (state.Profile is { } p)
        {
            var e = p.Embeddings[0];
            SuggestedThreshold = e.SuggestedAcceptThreshold;
            ProfileDetails =
                $"Enrolled {p.CreatedUtc.LocalDateTime:g}; {p.Enrollment.AcceptedSamples} samples; model {e.EncoderId} {e.EncoderVersion}; " +
                $"consent {p.Consent.ConsentTextVersion} accepted {p.Consent.AcceptedUtc.LocalDateTime:d}.";
        }

        RequireOwnerMatch = s.Verification.RequireOwnerMatch;
        UseCustomThreshold = s.Verification.AcceptThresholdOverride is not null;
        Threshold = s.Verification.AcceptThresholdOverride ?? (SuggestedThreshold > 0 ? SuggestedThreshold : 0.75);
        ExtractionEnabled = s.TargetSpeakerExtractionEnabled;
        ExtractionStatus = runtime.Status.TargetSpeakerExtraction.Available
            ? "A target-speaker extraction model is installed."
            : "No target-speaker extraction model is installed. Other voices are rejected only after the fact (speaker verification); overlapping speech is not removed.";
    }, "Could not load voice settings");

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        await settings.UpdateAsync(s => s with
        {
            Verification = s.Verification with
            {
                RequireOwnerMatch = RequireOwnerMatch,
                AcceptThresholdOverride = UseCustomThreshold ? Math.Round(Threshold, 2) : null,
            },
            TargetSpeakerExtractionEnabled = ExtractionEnabled,
        }, CancellationToken.None);
        await dialogs.AlertAsync("Saved", "Owner voice settings saved.");
    }, "Could not save");

    [RelayCommand]
    private Task ReEnrollAsync() => navigation.GoToAsync(Routes.Welcome);

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync("Delete voice profile",
                "Delete the enrolled voice profile and its encryption key from this phone? Voice search will need a new enrollment.",
                "Delete", "Cancel"))
        {
            return;
        }

        await profiles.DeleteAsync(CancellationToken.None);
        HasProfile = false;
        ProfileStatus = "No owner voice is enrolled.";
        ProfileDetails = string.Empty;
        await dialogs.AlertAsync("Deleted", "The voice profile and its key were deleted.");
    }, "Could not delete the profile");
}
