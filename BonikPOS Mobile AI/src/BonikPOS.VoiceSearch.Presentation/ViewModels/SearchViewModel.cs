using System.Collections.ObjectModel;
using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.MedicineSearch.Ranking;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

public enum SearchScreenState
{
    Preparing,
    Idle,
    Listening,
    HearingSpeech,
    Processing,
    Results,
}

/// <summary>
/// The medicine voice-search home screen: microphone, listening and processing indicators, recognised text with
/// correction, ranked results and confirmation. Nothing is ever selected without the owner confirming it.
/// </summary>
public sealed partial class SearchViewModel : ViewModelBase, IDisposable
{
    private readonly IVoiceSearchPipeline _pipeline;
    private readonly IVoiceModelRuntime _runtime;
    private readonly IMedicineSearchService _search;
    private readonly ICatalogManager _catalog;
    private readonly OwnerProfileService _profiles;
    private readonly SettingsService _settings;
    private readonly ISearchHistoryStore _history;
    private readonly SessionState _session;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly IPermissionService _permissions;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private MedicineSearchResult? _lastResult;
    private SpeechRecognitionResult? _lastRecognition;
    private OwnerVerificationResult? _lastVerification;
    private string? _recognizedOriginal;
    private bool _prepared;

    public SearchViewModel(
        IVoiceSearchPipeline pipeline,
        IVoiceModelRuntime runtime,
        IMedicineSearchService search,
        ICatalogManager catalog,
        OwnerProfileService profiles,
        SettingsService settings,
        ISearchHistoryStore history,
        SessionState session,
        INavigationService navigation,
        IDialogService dialogs,
        IPermissionService permissions,
        IUiDispatcher ui,
        TimeProvider time)
    {
        _pipeline = pipeline;
        _runtime = runtime;
        _search = search;
        _catalog = catalog;
        _profiles = profiles;
        _settings = settings;
        _history = history;
        _session = session;
        _navigation = navigation;
        _dialogs = dialogs;
        _permissions = permissions;
        _ui = ui;
        _time = time;
        _pipeline.EventRaised += OnPipelineEvent;
    }

    public ObservableCollection<ResultItemViewModel> Results { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListening), nameof(IsProcessing), nameof(MicButtonText), nameof(HasResults))]
    [NotifyCanExecuteChangedFor(nameof(ToggleListeningCommand))]
    public partial SearchScreenState State { get; set; } = SearchScreenState.Preparing;

    [ObservableProperty]
    public partial double Level { get; set; }

    [ObservableProperty]
    public partial bool SpeechActive { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Preparing...";

    [ObservableProperty]
    public partial string ExtractionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ExtractionActive { get; set; }

    [ObservableProperty]
    public partial string OwnerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecognizedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecognitionDetails { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowLowConfidence { get; set; }

    [ObservableProperty]
    public partial string LowConfidenceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DecisionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Explanation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CatalogIsDemo { get; set; }

    [ObservableProperty]
    public partial string CatalogText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleListeningCommand))]
    public partial bool VoiceAvailable { get; set; }

    [ObservableProperty]
    public partial string VoiceUnavailableReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Notice { get; set; }

    public bool IsListening => State is SearchScreenState.Listening or SearchScreenState.HearingSpeech;

    public bool IsProcessing => State == SearchScreenState.Processing;

    public bool HasResults => Results.Count > 0;

    public string MicButtonText => IsListening ? "Stop listening" : "Tap to speak";

    [RelayCommand]
    private Task AppearingAsync() => RunAsync(async () =>
    {
        bool searchNow = false;
        if (_session.TextToEdit is { } text)
        {
            RecognizedText = text;
            _session.TextToEdit = null;
            searchNow = _session.SearchTextOnReturn;
            _session.SearchTextOnReturn = false;
        }

        if (_session.LastSelectionMessage is { } selected)
        {
            Notice = selected;
            _session.LastSelectionMessage = null;
        }

        if (_prepared)
        {
            await RefreshVoiceAvailabilityAsync();
            if (searchNow)
            {
                await SearchTextCoreAsync();
            }

            return;
        }

        State = SearchScreenState.Preparing;
        StatusText = "Loading on-device models...";
        var settings = await _settings.LoadAsync(CancellationToken.None);
        await _runtime.InitializeAsync(settings, CancellationToken.None);
        if (!_catalog.Info.IsLoaded)
        {
            try
            {
                await _catalog.ReloadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                ErrorMessage = "The medicine catalog could not be loaded: " + ex.Message;
            }
        }

        UpdateCatalogText();
        var profile = await _profiles.GetStateAsync(CancellationToken.None);
        if (profile.Profile is null && profile.Compatibility == ProfileCompatibility.NoProfile)
        {
            _prepared = true;
            State = SearchScreenState.Idle;
            await _navigation.GoToAsync(Routes.Welcome);
            return;
        }

        _prepared = true;
        await RefreshVoiceAvailabilityAsync();
        State = SearchScreenState.Idle;
    }, "Could not prepare voice search");

    [RelayCommand]
    private async Task DisappearingAsync()
    {
        if (IsListening)
        {
            await _pipeline.StopAsync(CancellationToken.None);
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleListening))]
    private Task ToggleListeningAsync() => RunAsync(async () =>
    {
        if (IsListening)
        {
            await _pipeline.StopAsync(CancellationToken.None);
            return;
        }

        var permission = await _permissions.CheckMicrophoneAsync();
        if (permission != PermissionStatus.Granted)
        {
            permission = await _permissions.RequestMicrophoneAsync();
        }

        if (permission != PermissionStatus.Granted)
        {
            ErrorMessage = permission == PermissionStatus.DeniedPermanently
                ? "Microphone access is off. Enable it in the system settings for BonikPOS Voice Search."
                : "Microphone access is needed for voice search.";
            return;
        }

        ClearResults();
        Notice = null;
        var settings = await _settings.LoadAsync(CancellationToken.None);
        var profile = await _profiles.GetProfileAsync(CancellationToken.None);
        var reference = OwnerProfileService.BuildReference(profile, _runtime.SpeakerEncoder?.Info, settings.Verification);
        await _pipeline.StartAsync(new PipelineSessionOptions
        {
            Settings = settings,
            Owner = reference,
            ExtractionTarget = OwnerProfileService.ExtractionTarget(profile, _runtime.ExtractionRequiredEncoderId),
        }, CancellationToken.None);
        ExtractionActive = _pipeline.ExtractionStatus?.IsActive == true;
        ExtractionText = UserMessages.Describe(_pipeline.ExtractionStatus);
        OwnerText = reference is null ? "Owner voice check unavailable - results need extra care" : "Owner voice check ON";
    }, "Could not start listening");

    private bool CanToggleListening() => VoiceAvailable && State is not (SearchScreenState.Preparing or SearchScreenState.Processing);

    /// <summary>Searches the (possibly corrected) text without speaking again.</summary>
    [RelayCommand]
    private Task SearchTextAsync() => RunAsync(SearchTextCoreAsync, "Search failed");

    private async Task SearchTextCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(RecognizedText))
        {
            return;
        }

        bool corrected = _recognizedOriginal is not null && !string.Equals(_recognizedOriginal, RecognizedText, StringComparison.Ordinal);
        var source = corrected ? QuerySource.Correction : _recognizedOriginal is null ? QuerySource.Typed : QuerySource.Voice;
        var query = new MedicineQuery(RecognizedText, source,
            source == QuerySource.Voice ? _lastRecognition?.Confidence : null,
            source == QuerySource.Voice ? _lastVerification?.Decision : null);
        var result = await _search.SearchAsync(query, CancellationToken.None);
        if (source != QuerySource.Voice)
        {
            _lastRecognition = null;
            _lastVerification = null;
        }

        await ApplyResultAsync(result);
    }

    [RelayCommand]
    private Task RepeatAsync()
    {
        ClearResults();
        RecognizedText = string.Empty;
        _recognizedOriginal = null;
        return ToggleListeningCommand.CanExecute(null) ? ToggleListeningAsync() : Task.CompletedTask;
    }

    [RelayCommand]
    private Task ConfirmAsync(ResultItemViewModel? item) => RunAsync(async () =>
    {
        if (item is null || _lastResult is null)
        {
            return;
        }

        var p = item.Candidate.Product;
        bool corrected = _lastResult.Query.Source == QuerySource.Correction;
        await AddHistoryAsync(_lastResult, corrected ? HistoryOutcome.Corrected : HistoryOutcome.Confirmed, p);
        Notice = $"Selected {SearchDecisionPolicy.Label(p)} (ID {p.ProductId}).";
        ClearResults();
        RecognizedText = string.Empty;
        _recognizedOriginal = null;
    }, "Could not record the selection");

    [RelayCommand]
    private Task DismissAsync() => RunAsync(async () =>
    {
        if (_lastResult is not null)
        {
            await AddHistoryAsync(_lastResult, HistoryOutcome.Dismissed, null);
        }

        ClearResults();
    }, "Could not dismiss");

    [RelayCommand]
    private Task OpenDiagnosticsAsync() => _navigation.GoToAsync(Routes.Diagnostics);

    public void Dispose() => _pipeline.EventRaised -= OnPipelineEvent;

    internal async Task ApplyResultAsync(MedicineSearchResult result)
    {
        _lastResult = result;
        Results.Clear();
        int rank = 1;
        foreach (var c in result.Candidates)
        {
            Results.Add(new ResultItemViewModel(c, rank++));
        }

        OnPropertyChanged(nameof(HasResults));
        DecisionText = UserMessages.Describe(result.Decision);
        Explanation = result.Explanation;
        bool lowAsr = _lastRecognition is { } r && r.Confidence < _search.Thresholds.MinRecognitionConfidence;
        bool uncertainSpeaker = _lastVerification?.Decision is OwnerVoiceDecision.Uncertain;
        ShowLowConfidence = result.Decision == SearchDecision.LowConfidence || lowAsr || uncertainSpeaker;
        LowConfidenceText = lowAsr
            ? "Speech recognition was not confident. Check the text above and correct it if needed."
            : uncertainSpeaker
                ? UserMessages.Describe(_lastVerification) + "."
                : result.Decision == SearchDecision.LowConfidence ? result.Explanation : string.Empty;
        State = SearchScreenState.Results;
        StatusText = UserMessages.Describe(result.Decision);
        UpdateCatalogText();

        if (result.Decision is SearchDecision.Ambiguous or SearchDecision.NoMatch)
        {
            _session.PendingResult = result;
            _session.PendingRecognition = _lastRecognition;
            _session.PendingVerification = _lastVerification;
            await _navigation.GoToAsync(result.Decision == SearchDecision.Ambiguous ? Routes.Ambiguity : Routes.NoMatch);
        }
    }

    private async Task RefreshVoiceAvailabilityAsync()
    {
        var status = _runtime.Status;
        var profile = await _profiles.GetStateAsync(CancellationToken.None);
        var reasons = new List<string>();
        if (!status.VoiceActivity.Available)
        {
            reasons.Add("voice activity model missing");
        }

        if (!_runtime.Recognizer.Info.IsAvailable)
        {
            reasons.Add("speech-recognition model not installed");
        }

        if (!profile.IsUsable)
        {
            reasons.Add(profile.Message);
        }

        VoiceAvailable = status.VoiceActivity.Available && _runtime.Recognizer.Info.IsAvailable && profile.IsUsable;
        VoiceUnavailableReason = VoiceAvailable ? string.Empty : "Voice search unavailable: " + string.Join("; ", reasons) + ". You can still type a medicine name.";
        if (_runtime.ExtractionRequiredEncoderId is { } requiredEncoder)
        {
            bool ready = OwnerProfileService.ExtractionTarget(profile.Profile, requiredEncoder) is not null;
            ExtractionText = ready ? "Owner-voice extraction ready" : "Owner-voice extraction installed - re-enroll to activate it";
        }
        else
        {
            ExtractionText = UserMessages.Describe(_runtime.CreateTargetSpeakerExtractorStatusPreview());
        }
        OwnerText = profile.IsUsable ? "Owner voice enrolled" : profile.Message;
        if (State is SearchScreenState.Idle or SearchScreenState.Preparing)
        {
            StatusText = VoiceAvailable ? "Tap the microphone and say the medicine name" : "Type a medicine name";
        }
    }

    private void OnPipelineEvent(object? sender, PipelineEventArgs e) => _ui.Post(() => Handle(e.Event));

    private void Handle(PipelineEvent e)
    {
        switch (e)
        {
            case PipelineStateChangedEvent s:
                State = s.State switch
                {
                    PipelineState.Listening => SearchScreenState.Listening,
                    PipelineState.HearingSpeech => SearchScreenState.HearingSpeech,
                    PipelineState.Processing => SearchScreenState.Processing,
                    PipelineState.Starting => SearchScreenState.Listening,
                    _ => Results.Count > 0 ? SearchScreenState.Results : SearchScreenState.Idle,
                };
                StatusText = s.State switch
                {
                    PipelineState.Listening => "Listening - say the medicine name",
                    PipelineState.HearingSpeech => "Hearing speech...",
                    PipelineState.Processing => "Processing...",
                    PipelineState.Faulted => "Stopped because of an error",
                    _ => Results.Count > 0 ? StatusText : "Tap the microphone and say the medicine name",
                };
                if (s.State == PipelineState.Faulted && s.Detail is not null)
                {
                    ErrorMessage = s.Detail;
                }

                if (s.State is PipelineState.Idle or PipelineState.Faulted)
                {
                    Level = 0;
                    SpeechActive = false;
                }

                break;
            case AudioLevelEvent level:
                Level = LevelFraction(level.RmsDbfs);
                SpeechActive = level.InSpeech;
                break;
            case UtteranceIgnoredEvent ignored:
                Notice = ignored.Verification.Decision == OwnerVoiceDecision.NotOwner
                    ? "Ignored speech that did not match the owner's voice."
                    : ignored.Reason;
                break;
            case NothingRecognizedEvent:
                Notice = "Didn't catch a medicine name. Please say it again.";
                break;
            case ListeningTimedOutEvent:
                Notice = "No speech heard. Tap the microphone to try again.";
                break;
            case PipelineErrorEvent err:
                ErrorMessage = err.Message;
                break;
            case SearchCompletedEvent done:
                _lastRecognition = done.Recognition;
                _lastVerification = done.Verification;
                _recognizedOriginal = done.Recognition.Text;
                RecognizedText = done.Recognition.Text;
                RecognitionDetails =
                    $"{(done.Recognition.Language == "bn" ? "Bangla" : "English")} · confidence {done.Recognition.Confidence:P0} · " +
                    $"result in {done.Timings.EndOfSpeechToResultMs / 1000:0.0} s after you stopped speaking";
                OwnerText = UserMessages.Describe(done.Verification);
                ExtractionActive = done.Extraction.IsActive;
                ExtractionText = UserMessages.Describe(done.Extraction);
                _ = ApplyResultAsync(done.Search);
                break;
        }
    }

    private async Task AddHistoryAsync(MedicineSearchResult result, HistoryOutcome outcome, MedicineProduct? product)
    {
        var settings = await _settings.LoadAsync(CancellationToken.None);
        if (!settings.HistoryEnabled)
        {
            return;
        }

        await _history.AddAsync(new SearchHistoryEntry(
            Guid.NewGuid(), _time.GetUtcNow(), result.Query.Text, result.Query.Source, result.Decision, outcome,
            product?.ProductId, product is null ? null : SearchDecisionPolicy.Label(product),
            result.Query.RecognitionConfidence, result.Query.SpeakerDecision), CancellationToken.None);
    }

    private void ClearResults()
    {
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        _lastResult = null;
        DecisionText = string.Empty;
        Explanation = string.Empty;
        ShowLowConfidence = false;
        RecognitionDetails = string.Empty;
        if (State == SearchScreenState.Results)
        {
            State = SearchScreenState.Idle;
        }
    }

    private void UpdateCatalogText()
    {
        var info = _catalog.Info;
        CatalogIsDemo = info.IsDemoData;
        CatalogText = !info.IsLoaded
            ? "Catalog not loaded"
            : info.IsDemoData
                ? $"DEMO catalog ({info.ProductCount} sample products) - not real stock data"
                : $"{info.SourceDescription}: {info.ProductCount} products";
    }
}
