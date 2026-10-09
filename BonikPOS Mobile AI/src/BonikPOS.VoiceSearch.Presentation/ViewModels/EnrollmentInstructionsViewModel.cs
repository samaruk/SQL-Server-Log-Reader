using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>Voice enrollment instructions.</summary>
public sealed partial class EnrollmentInstructionsViewModel(INavigationService navigation) : ViewModelBase
{
    public IReadOnlyList<string> Steps { get; } =
    [
        "Find a quiet moment when no customer is talking near you.",
        "Hold the phone the way you will hold it at the counter (about 20-40 cm away).",
        "Read each sentence in your normal voice. Mix Bangla and English as you usually do.",
        "Record at least 5 good samples. The app tells you if a sample needs to be recorded again.",
        "Only you should speak while recording. The profile must contain only the owner's voice.",
    ];

    public string BanglaHint { get; } = "নিজের স্বাভাবিক কণ্ঠে বাক্যগুলো পড়ুন। রেকর্ডিংয়ের সময় অন্য কেউ যেন কথা না বলে।";

    [RelayCommand]
    private Task ContinueAsync() => navigation.GoToAsync(Routes.EnrollmentRecording);
}
