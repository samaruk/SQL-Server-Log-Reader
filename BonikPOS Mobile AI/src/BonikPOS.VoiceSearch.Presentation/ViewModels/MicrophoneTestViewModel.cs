using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Enrollment;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>Microphone permission (requested only here, when it is needed) and a short audio test.</summary>
public sealed partial class MicrophoneTestViewModel(
    IPermissionService permissions,
    IMicrophoneCapture capture,
    IVoiceModelRuntime runtime,
    SettingsService settings,
    INavigationService navigation,
    IUiDispatcher ui) : ViewModelBase
{
    public static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(6);

    [ObservableProperty]
    public partial PermissionStatus Permission { get; set; } = PermissionStatus.Unknown;

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    [ObservableProperty]
    public partial double Level { get; set; }

    [ObservableProperty]
    public partial string Instruction { get; set; } = "Tap \"Start test\". Stay quiet for 2 seconds, then say: \"Napa Extra five hundred\".";

    [ObservableProperty]
    public partial string? ResultSummary { get; set; }

    [ObservableProperty]
    public partial string? DeviceSummary { get; set; }

    [ObservableProperty]
    public partial bool TestPassed { get; set; }

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    public bool PermissionGranted => Permission == PermissionStatus.Granted;

    public bool PermissionPermanentlyDenied => Permission == PermissionStatus.DeniedPermanently;

    partial void OnPermissionChanged(PermissionStatus value)
    {
        OnPropertyChanged(nameof(PermissionGranted));
        OnPropertyChanged(nameof(PermissionPermanentlyDenied));
    }

    [RelayCommand]
    private async Task AppearingAsync() => Permission = await permissions.CheckMicrophoneAsync();

    [RelayCommand]
    private async Task RequestPermissionAsync() => Permission = await permissions.RequestMicrophoneAsync();

    [RelayCommand]
    private void OpenAppSettings() => permissions.OpenAppSettings();

    [RelayCommand]
    private Task StartTestAsync() => RunAsync(async () =>
    {
        if (Permission != PermissionStatus.Granted)
        {
            Permission = await permissions.RequestMicrophoneAsync();
            if (Permission != PermissionStatus.Granted)
            {
                return;
            }
        }

        await runtime.InitializeAsync(await settings.LoadAsync(CancellationToken.None), CancellationToken.None);
        IsTesting = true;
        HasResult = false;
        Instruction = "Stay quiet...";
        var progress = new Progress<LevelReading>(r => ui.Post(() =>
        {
            Level = LevelFraction(r.RmsDbfs);
            Instruction = r.ElapsedSeconds < 2 ? "Stay quiet..." : "Now say: \"Napa Extra five hundred\"";
        }));
        try
        {
            var result = await new MicrophoneTester(capture, runtime).RunAsync(TestDuration, progress, CancellationToken.None);
            ShowResult(result);
        }
        finally
        {
            IsTesting = false;
            Level = 0;
        }
    }, "Microphone test failed");

    [RelayCommand]
    private Task ContinueAsync() => navigation.GoToAsync(Routes.EnrollmentInstructions);

    private void ShowResult(MicrophoneTestResult result)
    {
        var q = result.Quality;
        TestPassed = q.IsAcceptable;
        HasResult = true;
        string verdict = q.IsAcceptable
            ? "Good: your voice is clear enough for enrollment."
            : "Please improve: " + string.Join(", ", q.Issues.Select(UserMessages.Describe)) + ".";
        ResultSummary =
            $"{verdict}\nSpeech level {q.SpeechDbfs:0} dBFS, background {q.NoiseFloorDbfs:0} dBFS, signal-to-noise {q.EstimatedSnrDb:0} dB, " +
            $"clipping {q.ClippingRatio:P2}.";
        var c = result.Capture;
        DeviceSummary = c is null
            ? null
            : $"Microphone: {c.DeviceSampleRate} Hz via {c.InputPath}{(c.SoftwareResampling ? ", resampled to 16 kHz" : string.Empty)}; " +
              $"dropped frames: {result.Queue.FramesDropped}.";
        Instruction = q.IsAcceptable ? "Microphone test passed." : "Try again in a quieter place or adjust the distance.";
    }
}
