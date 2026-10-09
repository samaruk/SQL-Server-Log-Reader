using System.Collections.ObjectModel;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Enrollment;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>Instructions, multiple sample recordings with quality feedback, and enrollment progress.</summary>
public sealed partial class EnrollmentViewModel(
    IMicrophoneCapture capture,
    IVoiceModelRuntime runtime,
    IAudioOutput audioOutput,
    SettingsService settings,
    SessionState session,
    INavigationService navigation,
    IDialogService dialogs,
    IAppInfoService appInfo,
    IUiDispatcher ui) : ViewModelBase, IDisposable
{
    private readonly List<EnrollmentPrompt> _prompts = [.. EnrollmentPrompts.Default];
    private readonly List<EnrollmentSampleResult> _results = [];
    private EnrollmentSession? _session;
    private CancellationTokenSource? _stop;
    private CancellationTokenSource? _abort;
    private int _promptIndex;

    public ObservableCollection<string> Feedback { get; } = [];

    [ObservableProperty]
    public partial EnrollmentPrompt? CurrentPrompt { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(StopRecordingCommand), nameof(FinishCommand), nameof(PlayLastCommand))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial double Level { get; set; }

    [ObservableProperty]
    public partial bool SpeechDetected { get; set; }

    [ObservableProperty]
    public partial double Elapsed { get; set; }

    [ObservableProperty]
    public partial int AcceptedSamples { get; set; }

    [ObservableProperty]
    public partial int RequiredSamples { get; set; } = new EnrollmentOptions().RequiredSamples;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    public partial bool CanFinish { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayLastCommand))]
    public partial bool HasLastSample { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAcceptedFeedback), nameof(ShowRejectedFeedback))]
    public partial bool? LastSampleAccepted { get; set; }

    /// <summary>True right after a sample passed the quality checks.</summary>
    public bool ShowAcceptedFeedback => LastSampleAccepted == true;

    /// <summary>True right after a sample was rejected; the feedback explains why.</summary>
    public bool ShowRejectedFeedback => LastSampleAccepted == false;

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [RelayCommand]
    private Task AppearingAsync() => RunAsync(async () =>
    {
        await runtime.InitializeAsync(await settings.LoadAsync(CancellationToken.None), CancellationToken.None);
        if (runtime.SpeakerEncoder is null)
        {
            ErrorMessage = "The speaker model is not installed. See Diagnostics.";
            return;
        }

        if (_session is null)
        {
            _session = new EnrollmentSession(capture, runtime);
            _prompts.Add(EnrollmentPrompts.CreateSpokenCodePrompt(Random.Shared));
            _promptIndex = 0;
            CurrentPrompt = _prompts[0];
            UpdateProgress();
        }
    }, "Could not prepare enrollment");

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private Task StartRecordingAsync() => RunAsync(async () =>
    {
        if (_session is null || CurrentPrompt is null)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        _abort = new CancellationTokenSource();
        IsRecording = true;
        Feedback.Clear();
        LastSampleAccepted = null;
        var progress = new Progress<LevelReading>(r => ui.Post(() =>
        {
            Level = LevelFraction(r.RmsDbfs);
            SpeechDetected = r.SpeechDetected;
            Elapsed = r.ElapsedSeconds;
        }));
        try
        {
            var result = await _session.RecordSampleAsync(CurrentPrompt, progress, _stop.Token, _abort.Token);
            _results.Add(result);
            HasLastSample = true;
            LastSampleAccepted = result.Accepted;
            if (result.Accepted)
            {
                Feedback.Add($"Sample {result.Number} accepted ({result.Quality.SpeechSeconds:0.0} s of speech, SNR {result.Quality.EstimatedSnrDb:0} dB).");
                NextPrompt();
            }
            else
            {
                foreach (var issue in result.Issues)
                {
                    Feedback.Add(UserMessages.Describe(issue));
                }
            }

            UpdateProgress();
        }
        finally
        {
            IsRecording = false;
            Level = 0;
            SpeechDetected = false;
            _stop.Dispose();
            _abort.Dispose();
            _stop = _abort = null;
        }
    }, "Recording failed");

    private bool CanStartRecording() => !IsRecording;

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private void StopRecording() => _stop?.Cancel();

    [RelayCommand(CanExecute = nameof(CanPlayLast))]
    private Task PlayLastAsync() => RunAsync(
        () => audioOutput.PlayAsync(_session!.LastRecording, 16000, CancellationToken.None), "Playback failed");

    private bool CanPlayLast() => HasLastSample && !IsRecording && _session is not null;

    [RelayCommand]
    private void SkipPrompt() => NextPrompt();

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private Task FinishAsync() => RunAsync(async () =>
    {
        if (_session is null)
        {
            return;
        }

        var consent = session.Consent ?? throw new InvalidOperationException("Consent was not recorded. Start again from the welcome screen.");
        var profile = _session.Complete(consent, appInfo.Version);
        var embedding = profile.Embeddings[0];
        session.EnrollmentDraft = profile;
        session.EnrollmentCalibration = (embedding.MeanSampleSimilarity, embedding.MinSampleSimilarity, 0, embedding.SuggestedAcceptThreshold);
        session.EnrollmentSamples = [.. _results];
        DisposeSession();
        await navigation.GoToAsync(Routes.EnrollmentResult);
    }, "Could not complete enrollment");

    private bool CanComplete() => CanFinish && !IsRecording;

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (!await dialogs.ConfirmAsync("Cancel enrollment", "Discard the recorded samples? Nothing has been saved.", "Discard", "Keep recording"))
        {
            return;
        }

        _abort?.Cancel();
        DisposeSession();
        await navigation.GoBackAsync();
    }

    public void Dispose()
    {
        _abort?.Cancel();
        DisposeSession();
    }

    private void NextPrompt()
    {
        _promptIndex++;
        if (_promptIndex >= _prompts.Count)
        {
            _prompts.Add(EnrollmentPrompts.CreateSpokenCodePrompt(Random.Shared));
        }

        CurrentPrompt = _prompts[_promptIndex];
    }

    private void UpdateProgress()
    {
        var p = _session?.Progress;
        AcceptedSamples = p?.Accepted ?? 0;
        RequiredSamples = p?.Required ?? RequiredSamples;
        Progress = RequiredSamples == 0 ? 0 : Math.Min(1, (double)AcceptedSamples / RequiredSamples);
        CanFinish = p?.CanComplete ?? false;
        ProgressText = $"{AcceptedSamples} of {RequiredSamples} samples accepted" + (p is { Rejected: > 0 } ? $" ({p.Rejected} to re-record)" : string.Empty);
    }

    private void DisposeSession()
    {
        _session?.Dispose(); // clears in-memory audio
        _session = null;
    }
}
