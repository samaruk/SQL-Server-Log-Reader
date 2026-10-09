using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>Catalog connection, recognition, audio, privacy and advanced matching settings.</summary>
public sealed partial class SettingsViewModel(
    SettingsService settings,
    ISecretStore secrets,
    ICatalogManager catalog,
    IVoiceModelRuntime runtime,
    IPrivacyService privacy,
    IFilePickerService filePicker,
    INavigationService navigation,
    IDialogService dialogs) : ViewModelBase
{
    private const string TokenPlaceholder = "••••••••";

    public IReadOnlyList<Choice<CatalogSourceKind>> CatalogSources { get; } = Choices.Of(
        (CatalogSourceKind.Demo, "Demo catalog (test data)"),
        (CatalogSourceKind.LocalFile, "Local product index file"),
        (CatalogSourceKind.BonikPosApi, "BonikPOS API"));

    public IReadOnlyList<Choice<RecognitionLanguageMode>> LanguageModes { get; } = Choices.Of(
        (RecognitionLanguageMode.AutoBanglaEnglish, "Automatic (Bangla or English)"),
        (RecognitionLanguageMode.Bangla, "Bangla (including Banglish)"),
        (RecognitionLanguageMode.English, "English"));

    public IReadOnlyList<Choice<AudioInputPreference>> AudioInputs { get; } = Choices.Of(
        (AudioInputPreference.VoiceRecognition, "Speech recognition input (recommended)"),
        (AudioInputPreference.Unprocessed, "Unprocessed microphone (if supported)"),
        (AudioInputPreference.Default, "Default microphone"));

    public IReadOnlyList<Choice<AcceleratorPreference>> Accelerators { get; } = Choices.Of(
        (AcceleratorPreference.Auto, "Automatic (use the phone's accelerator if available)"),
        (AcceleratorPreference.CpuOnly, "CPU only"));

    [ObservableProperty]
    public partial Choice<CatalogSourceKind>? SelectedCatalogSource { get; set; }

    [ObservableProperty]
    public partial Choice<RecognitionLanguageMode>? SelectedLanguageMode { get; set; }

    [ObservableProperty]
    public partial Choice<AudioInputPreference>? SelectedAudioInput { get; set; }

    [ObservableProperty]
    public partial Choice<AcceleratorPreference>? SelectedAccelerator { get; set; }

    partial void OnSelectedCatalogSourceChanged(Choice<CatalogSourceKind>? value) => CatalogSource = value?.Value ?? CatalogSourceKind.Demo;

    partial void OnCatalogSourceChanged(CatalogSourceKind value)
    {
        if (SelectedCatalogSource?.Value != value)
        {
            SelectedCatalogSource = Choices.Find(CatalogSources, value);
        }
    }

    public IReadOnlyList<string> AuthSchemes { get; } = ["Bearer", "ApiKey"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApi), nameof(IsLocalFile))]
    public partial CatalogSourceKind CatalogSource { get; set; }

    public bool IsApi => CatalogSource == CatalogSourceKind.BonikPosApi;

    public bool IsLocalFile => CatalogSource == CatalogSourceKind.LocalFile;

    [ObservableProperty]
    public partial string? LocalCatalogPath { get; set; }

    [ObservableProperty]
    public partial string? ApiBaseUrl { get; set; }

    [ObservableProperty]
    public partial string ProductsPath { get; set; } = "api/products";

    [ObservableProperty]
    public partial string AuthScheme { get; set; } = "Bearer";

    [ObservableProperty]
    public partial string ApiKeyHeader { get; set; } = "X-Api-Key";

    [ObservableProperty]
    public partial string? ApiToken { get; set; }

    [ObservableProperty]
    public partial string FieldItems { get; set; } = "items";

    [ObservableProperty]
    public partial string FieldProductId { get; set; } = "productId";

    [ObservableProperty]
    public partial string FieldBrandName { get; set; } = "brandName";

    [ObservableProperty]
    public partial string FieldGenericName { get; set; } = "genericName";

    [ObservableProperty]
    public partial string FieldStrength { get; set; } = "strength";

    [ObservableProperty]
    public partial string FieldDosageForm { get; set; } = "dosageForm";

    [ObservableProperty]
    public partial RecognitionLanguageMode LanguageMode { get; set; }

    [ObservableProperty]
    public partial AudioInputPreference AudioInput { get; set; }

    [ObservableProperty]
    public partial AcceleratorPreference Accelerator { get; set; }

    [ObservableProperty]
    public partial bool HistoryEnabled { get; set; }

    [ObservableProperty]
    public partial double MinCandidateScore { get; set; }

    [ObservableProperty]
    public partial double ConfidentScore { get; set; }

    [ObservableProperty]
    public partial double AmbiguityMargin { get; set; }

    [ObservableProperty]
    public partial double MinRecognitionConfidence { get; set; }

    [ObservableProperty]
    public partial string CatalogStatus { get; set; } = string.Empty;

    [RelayCommand]
    private Task AppearingAsync() => RunAsync(async () =>
    {
        var s = await settings.LoadAsync(CancellationToken.None);
        CatalogSource = s.CatalogSource;
        LocalCatalogPath = s.LocalCatalogPath;
        ApiBaseUrl = s.Api.BaseUrl;
        ProductsPath = s.Api.ProductsPath;
        AuthScheme = s.Api.AuthScheme;
        ApiKeyHeader = s.Api.ApiKeyHeader;
        FieldItems = s.Api.Fields.ItemsProperty;
        FieldProductId = s.Api.Fields.ProductId;
        FieldBrandName = s.Api.Fields.BrandName;
        FieldGenericName = s.Api.Fields.GenericName;
        FieldStrength = s.Api.Fields.Strength;
        FieldDosageForm = s.Api.Fields.DosageForm;
        ApiToken = await secrets.GetAsync(SecretKeys.ApiToken) is null ? null : TokenPlaceholder;
        LanguageMode = s.LanguageMode;
        AudioInput = s.AudioInput;
        Accelerator = s.Accelerator;
        SelectedCatalogSource = Choices.Find(CatalogSources, s.CatalogSource);
        SelectedLanguageMode = Choices.Find(LanguageModes, s.LanguageMode);
        SelectedAudioInput = Choices.Find(AudioInputs, s.AudioInput);
        SelectedAccelerator = Choices.Find(Accelerators, s.Accelerator);
        HistoryEnabled = s.HistoryEnabled;
        MinCandidateScore = s.Search.MinCandidateScore;
        ConfidentScore = s.Search.ConfidentScore;
        AmbiguityMargin = s.Search.AmbiguityMargin;
        MinRecognitionConfidence = s.Search.MinRecognitionConfidence;
        UpdateCatalogStatus();
    }, "Could not load settings");

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        LanguageMode = SelectedLanguageMode?.Value ?? LanguageMode;
        AudioInput = SelectedAudioInput?.Value ?? AudioInput;
        Accelerator = SelectedAccelerator?.Value ?? Accelerator;
        var before = await settings.LoadAsync(CancellationToken.None);
        var updated = await settings.UpdateAsync(s => s with
        {
            CatalogSource = CatalogSource,
            LocalCatalogPath = LocalCatalogPath,
            LanguageMode = LanguageMode,
            AudioInput = AudioInput,
            Accelerator = Accelerator,
            HistoryEnabled = HistoryEnabled,
            Search = s.Search with
            {
                MinCandidateScore = Math.Clamp(MinCandidateScore, 0.3, 0.9),
                ConfidentScore = Math.Clamp(ConfidentScore, 0.6, 1.0),
                AmbiguityMargin = Math.Clamp(AmbiguityMargin, 0, 0.3),
                MinRecognitionConfidence = Math.Clamp(MinRecognitionConfidence, 0, 1),
            },
            Api = s.Api with
            {
                BaseUrl = string.IsNullOrWhiteSpace(ApiBaseUrl) ? null : ApiBaseUrl.Trim(),
                ProductsPath = ProductsPath.Trim(),
                AuthScheme = AuthScheme,
                ApiKeyHeader = ApiKeyHeader.Trim(),
                Fields = s.Api.Fields with
                {
                    ItemsProperty = FieldItems.Trim(),
                    ProductId = FieldProductId.Trim(),
                    BrandName = FieldBrandName.Trim(),
                    GenericName = FieldGenericName.Trim(),
                    Strength = FieldStrength.Trim(),
                    DosageForm = FieldDosageForm.Trim(),
                },
            },
        }, CancellationToken.None);

        if (ApiToken != TokenPlaceholder)
        {
            if (string.IsNullOrWhiteSpace(ApiToken))
            {
                await secrets.RemoveAsync(SecretKeys.ApiToken);
            }
            else
            {
                await secrets.SetAsync(SecretKeys.ApiToken, ApiToken.Trim());
                ApiToken = TokenPlaceholder;
            }
        }

        if (before.Accelerator != updated.Accelerator || before.InferenceThreads != updated.InferenceThreads)
        {
            await runtime.ReloadAsync(updated, CancellationToken.None);
        }

        await ReloadCatalogAsync();
        await dialogs.AlertAsync("Saved", "Settings saved. " + CatalogStatus);
    }, "Could not save settings");

    [RelayCommand]
    private Task SyncCatalogAsync() => RunAsync(ReloadCatalogAsync, "Catalog sync failed");

    [RelayCommand]
    private Task ImportCatalogFileAsync() => RunAsync(async () =>
    {
        var path = await filePicker.PickCatalogFileAsync();
        if (path is null)
        {
            return;
        }

        LocalCatalogPath = path;
        CatalogSource = CatalogSourceKind.LocalFile;
    }, "Could not import the file");

    [RelayCommand]
    private Task DeleteAllDataAsync() => RunAsync(async () =>
    {
        if (!await dialogs.ConfirmAsync("Delete all data",
                "Delete the voice profile, search history, API token, cached catalog and settings from this phone? This cannot be undone.",
                "Delete everything", "Cancel"))
        {
            return;
        }

        await privacy.DeleteAllDataAsync(CancellationToken.None);
        await dialogs.AlertAsync("Deleted", "All app data was deleted. The app will start the welcome flow.");
        await navigation.GoToAsync(Routes.Welcome);
    }, "Could not delete data");

    [RelayCommand]
    private Task OpenVoiceSettingsAsync() => navigation.GoToAsync(Routes.VoiceSettings);

    [RelayCommand]
    private Task OpenDiagnosticsAsync() => navigation.GoToAsync(Routes.Diagnostics);

    [RelayCommand]
    private Task ShowPrivacyAsync() => dialogs.AlertAsync("Privacy", ConsentText.Summary +
        " Medicine results are search suggestions only - this app does not give medical advice, recommend doses or change prescriptions.");

    private async Task ReloadCatalogAsync()
    {
        try
        {
            await catalog.ReloadAsync(CancellationToken.None);
        }
        finally
        {
            UpdateCatalogStatus();
        }
    }

    private void UpdateCatalogStatus()
    {
        var i = catalog.Info;
        CatalogStatus = !i.IsLoaded
            ? "No catalog loaded."
            : $"{i.SourceDescription}: {i.ProductCount} products" +
              (i.RetrievedAt is { } t ? $", loaded {t.LocalDateTime:g}" : string.Empty) +
              (i.Warnings.Count > 0 ? $". {i.Warnings.Count} warning(s): {i.Warnings[0]}" : ".");
    }
}
