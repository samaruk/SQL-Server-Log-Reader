using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>Welcome, purpose of voice enrollment, and consent.</summary>
public sealed partial class WelcomeViewModel(
    OwnerProfileService profiles, SessionState session, INavigationService navigation, TimeProvider time) : ViewModelBase
{
    public string ConsentSummary => ConsentText.Summary;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartEnrollmentCommand))]
    public partial bool ConsentAccepted { get; set; }

    [ObservableProperty]
    public partial bool HasUsableProfile { get; set; }

    [ObservableProperty]
    public partial string ProfileMessage { get; set; } = string.Empty;

    [RelayCommand]
    private Task AppearingAsync() => RunAsync(async () =>
    {
        var state = await profiles.GetStateAsync(CancellationToken.None);
        HasUsableProfile = state.IsUsable;
        ProfileMessage = state.Message;
    }, "Could not read the voice profile");

    [RelayCommand(CanExecute = nameof(ConsentAccepted))]
    private Task StartEnrollmentAsync()
    {
        session.Consent = new ConsentRecord(time.GetUtcNow(), ConsentText.Version);
        return navigation.GoToAsync(Routes.MicrophoneTest);
    }

    [RelayCommand]
    private Task ContinueToSearchAsync() => navigation.GoToAsync(Routes.Search);
}
