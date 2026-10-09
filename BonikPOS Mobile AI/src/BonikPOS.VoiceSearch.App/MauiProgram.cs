using BonikPOS.VoiceSearch.AI.Runtime;
using BonikPOS.VoiceSearch.App.Services;
using BonikPOS.VoiceSearch.App.Views;
using BonikPOS.VoiceSearch.Audio.Benchmark;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Benchmark;
using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Infrastructure.Catalog;
using BonikPOS.VoiceSearch.Infrastructure.Storage;
using BonikPOS.VoiceSearch.MedicineSearch;
using BonikPOS.VoiceSearch.MedicineSearch.Catalog;
using BonikPOS.VoiceSearch.Presentation.Services;
using BonikPOS.VoiceSearch.Presentation.ViewModels;
using Microsoft.Extensions.Logging;

namespace BonikPOS.VoiceSearch.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // First thing: ONNX Runtime's built-in usage telemetry must be off before the runtime initialises (docs/PRIVACY.md).
        BonikPOS.VoiceSearch.AI.Onnx.OnnxRuntimeTelemetry.Disable();
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif
        RegisterServices(builder.Services);
        RegisterViews(builder.Services);
        return builder.Build();
    }

    private static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAppPaths>(_ => MauiAppPaths.Create());
        services.AddSingleton<ISecretStore, MauiSecretStore>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IPerformanceTelemetry, PerformanceTelemetry>();

        // Audio: native capture behind IMicrophoneCapture.
#if ANDROID
        services.AddSingleton<IMicrophoneCapture>(sp => new Audio.Platforms.Android.AndroidMicrophoneCapture(
            Android.App.Application.Context, sp.GetRequiredService<ILogger<Audio.Platforms.Android.AndroidMicrophoneCapture>>()));
        services.AddSingleton<IAudioOutput, Audio.Platforms.Android.AndroidAudioOutput>();
#elif IOS
        services.AddSingleton<IMicrophoneCapture, Audio.Platforms.iOS.IosMicrophoneCapture>();
        services.AddSingleton<IAudioOutput, Audio.Platforms.iOS.IosAudioOutput>();
#endif

        // AI: models are extracted from the app package, verified and loaded once.
        services.AddSingleton<IModelProvider, BootstrappingModelProvider>();
        services.AddSingleton<IVoiceModelRuntime, OnnxVoiceModelRuntime>();
        services.AddSingleton<IVoiceSearchPipeline, VoiceSearchPipeline>();

        // Owner profile, history and catalog.
        services.AddSingleton<IVoiceProfileStore, EncryptedVoiceProfileStore>();
        services.AddSingleton<OwnerProfileService>();
        services.AddSingleton<EncryptedSearchHistoryStore>();
        services.AddSingleton<ISearchHistoryStore>(sp => sp.GetRequiredService<EncryptedSearchHistoryStore>());
        services.AddSingleton<IMedicineSearchHints>(sp => sp.GetRequiredService<EncryptedSearchHistoryStore>());
        services.AddSingleton<IMedicineSearchService>(sp => new MedicineSearchService(
            () => new DemoCatalogSource(), sp.GetRequiredService<IMedicineSearchHints>(), sp.GetRequiredService<ILogger<MedicineSearchService>>()));
        services.AddSingleton<ICatalogCache, FileCatalogCache>();
        services.AddSingleton(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }); // per-request timeouts are applied by the adapter
        services.AddSingleton<ICatalogManager>(sp => new CatalogManager(
            sp.GetRequiredService<IMedicineSearchService>(), sp.GetRequiredService<SettingsService>(), sp.GetRequiredService<ISecretStore>(),
            () => sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ICatalogCache>(), sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IPrivacyService, PrivacyService>();

        // Diagnostics: full-pipeline benchmark (replays clips; never uses the microphone, stores no audio).
        services.AddSingleton<IResourceSampler, MauiResourceSampler>();
        services.AddSingleton<IBenchmarkClipSource, MauiBenchmarkClipSource>();
        services.AddSingleton<IPipelineBenchmark>(sp => new PipelineBenchmark(
            sp.GetRequiredService<IVoiceModelRuntime>(), sp.GetRequiredService<IMedicineSearchService>(), sp.GetRequiredService<IResourceSampler>(),
            ct => sp.GetRequiredService<SettingsService>().LoadAsync(ct), sp.GetRequiredService<ILogger<PipelineBenchmark>>())
        {
            DeviceDescription = () => $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model} ({DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}); " +
                                      PipelineBenchmarkReport.DescribeRuntime() + BuildKind,
        });

        // UI services.
        services.AddSingleton<SessionState>();
        services.AddSingleton<INavigationService, ShellNavigationService>();
        services.AddSingleton<IDialogService, MauiDialogService>();
        services.AddSingleton<IPermissionService, MauiPermissionService>();
        services.AddSingleton<IUiDispatcher, MauiUiDispatcher>();
        services.AddSingleton<IClipboardService, MauiClipboardService>();
        services.AddSingleton<IFilePickerService, MauiFilePickerService>();
        services.AddSingleton<IAppInfoService, MauiAppInfoService>();
        services.AddSingleton<IDeviceStatusService, MauiDeviceStatusService>();
    }

#if DEBUG
    // Debug builds run managed code in the Mono interpreter (Hot Reload): C# signal processing and search are slower.
    private const string BuildKind = "; DEBUG build - managed code interpreted, use a Release build for reported numbers";
#else
    private const string BuildKind = "; Release build";
#endif

    private static void RegisterViews(IServiceCollection services)
    {
        // The search screen keeps its state while clarification screens are open.
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SearchPage>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<HistoryPage>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<WelcomeViewModel>();
        services.AddTransient<WelcomePage>();
        services.AddTransient<MicrophoneTestViewModel>();
        services.AddTransient<MicrophoneTestPage>();
        services.AddTransient<EnrollmentInstructionsViewModel>();
        services.AddTransient<EnrollmentInstructionsPage>();
        services.AddTransient<EnrollmentViewModel>();
        services.AddTransient<EnrollmentRecordingPage>();
        services.AddTransient<EnrollmentResultViewModel>();
        services.AddTransient<EnrollmentResultPage>();
        services.AddTransient<AmbiguityViewModel>();
        services.AddTransient<AmbiguityPage>();
        services.AddTransient<NoMatchViewModel>();
        services.AddTransient<NoMatchPage>();
        services.AddTransient<VoiceSettingsViewModel>();
        services.AddTransient<VoiceSettingsPage>();
        services.AddTransient<DiagnosticsViewModel>();
        services.AddTransient<DiagnosticsPage>();
    }
}
