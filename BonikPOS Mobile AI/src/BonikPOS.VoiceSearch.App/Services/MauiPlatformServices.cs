using System.Diagnostics;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Infrastructure.Models;
using BonikPOS.VoiceSearch.Infrastructure.Storage;
using BonikPOS.VoiceSearch.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace BonikPOS.VoiceSearch.App.Services;

public sealed class ShellNavigationService : INavigationService
{
    public Task GoToAsync(string route) => Shell.Current.GoToAsync(route);

    public Task GoBackAsync() => Shell.Current.GoToAsync("..");
}

public sealed class MauiDialogService : IDialogService
{
    public Task AlertAsync(string title, string message, string accept = "OK") =>
        Shell.Current.DisplayAlertAsync(title, message, accept);

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) =>
        Shell.Current.DisplayAlertAsync(title, message, accept, cancel);
}

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);
}

public sealed class MauiClipboardService : IClipboardService
{
    public Task SetTextAsync(string text) => Clipboard.Default.SetTextAsync(text);
}

/// <summary>Microphone permission via MAUI Essentials, with detection of "denied, don't ask again".</summary>
public sealed class MauiPermissionService : IPermissionService
{
    private const string RequestedKey = "permission.microphone.requested";

    public async Task<Presentation.Services.PermissionStatus> CheckMicrophoneAsync() =>
        Map(await Permissions.CheckStatusAsync<Permissions.Microphone>(), requested: Preferences.Default.Get(RequestedKey, false));

    public async Task<Presentation.Services.PermissionStatus> RequestMicrophoneAsync()
    {
        var status = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.Microphone>);
        Preferences.Default.Set(RequestedKey, true);
        return Map(status, requested: true);
    }

    public void OpenAppSettings() => AppInfo.Current.ShowSettingsUI();

    private static Presentation.Services.PermissionStatus Map(Microsoft.Maui.ApplicationModel.PermissionStatus status, bool requested)
    {
        if (status == Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
        {
            return Presentation.Services.PermissionStatus.Granted;
        }

        if (status is Microsoft.Maui.ApplicationModel.PermissionStatus.Denied or Microsoft.Maui.ApplicationModel.PermissionStatus.Restricted && requested)
        {
            // iOS asks only once; Android stops asking when the rationale is no longer shown.
            bool permanent = DeviceInfo.Current.Platform == DevicePlatform.iOS || !Permissions.ShouldShowRationale<Permissions.Microphone>();
            return permanent ? Presentation.Services.PermissionStatus.DeniedPermanently : Presentation.Services.PermissionStatus.Denied;
        }

        return status == Microsoft.Maui.ApplicationModel.PermissionStatus.Unknown
            ? Presentation.Services.PermissionStatus.Unknown
            : Presentation.Services.PermissionStatus.Denied;
    }
}

/// <summary>Android Keystore-backed / iOS Keychain storage through MAUI SecureStorage.</summary>
public sealed class MauiSecretStore : ISecretStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public Task RemoveAsync(string key)
    {
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class MauiFilePickerService : IFilePickerService
{
    public async Task<string?> PickCatalogFileAsync()
    {
        var types = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["application/json", "text/plain"],
            [DevicePlatform.iOS] = ["public.json", "public.plain-text"],
        });
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose a BonikPOS product index (JSON)", FileTypes = types });
        if (file is null)
        {
            return null;
        }

        string target = Path.Combine(FileSystem.AppDataDirectory, PrivacyService.ImportedCatalogFileName);
        await using var source = await file.OpenReadAsync();
        await using var dest = File.Create(target);
        await source.CopyToAsync(dest);
        return target;
    }
}

public sealed class MauiAppInfoService : IAppInfoService
{
    public string Version => $"{AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})";

    public string Platform => $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}";

    public string DeviceModel => $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}";
}

/// <summary>Battery, thermal state and process resource usage.</summary>
public sealed class MauiDeviceStatusService : IDeviceStatusService
{
    private TimeSpan _lastCpu;
    private long _lastWall;

    public DeviceStatus GetStatus()
    {
        double? battery = null;
        string batteryState = "unknown";
        bool? saver = null;
        try
        {
            battery = Battery.Default.ChargeLevel;
            batteryState = Battery.Default.State.ToString();
            saver = Battery.Default.EnergySaverStatus == EnergySaverStatus.On;
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
        {
        }

        var process = Process.GetCurrentProcess();
        double? cpu = null;
        var nowCpu = process.TotalProcessorTime;
        long nowWall = Stopwatch.GetTimestamp();
        if (_lastWall != 0)
        {
            double wall = Stopwatch.GetElapsedTime(_lastWall, nowWall).TotalMilliseconds;
            cpu = wall > 0 ? (nowCpu - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100 : null;
        }

        _lastCpu = nowCpu;
        _lastWall = nowWall;
        return new DeviceStatus(battery, batteryState, ThermalState(), saver,
            GC.GetTotalMemory(false) / 1048576.0, process.WorkingSet64 > 0 ? process.WorkingSet64 / 1048576.0 : null, cpu);
    }

    private static string ThermalState()
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(29)
            && Android.App.Application.Context.GetSystemService(Android.Content.Context.PowerService) is Android.OS.PowerManager pm)
        {
            return pm.CurrentThermalStatus.ToString();
        }

        return "unavailable (Android 10+ required)";
#elif IOS
        return Foundation.NSProcessInfo.ProcessInfo.ThermalState.ToString();
#else
        return "unavailable";
#endif
    }
}

/// <summary>Private app directories; profile and history are excluded from cloud backup on iOS.</summary>
public static class MauiAppPaths
{
    public static AppPaths Create()
    {
        string data = Path.Combine(FileSystem.AppDataDirectory, "bonikpos");
        string cache = Path.Combine(FileSystem.CacheDirectory, "bonikpos");
        string bundled = Path.Combine(FileSystem.AppDataDirectory, "models-bundled");
        string sideload =
#if ANDROID
            Path.Combine(Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath ?? FileSystem.AppDataDirectory, "models");
#else
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "models");
#endif
        foreach (var dir in new[] { data, cache, bundled })
        {
            Directory.CreateDirectory(dir);
        }

#if IOS
        foreach (var dir in new[] { data, bundled })
        {
            var url = Foundation.NSUrl.FromFilename(dir);
            url.SetResource(Foundation.NSUrl.IsExcludedFromBackupKey, Foundation.NSNumber.FromBoolean(true), out _);
        }
#endif
        return new AppPaths(data, cache, bundled, sideload);
    }
}

public sealed class MauiAppPackageFiles : IAppPackageFiles
{
    public Task<Stream> OpenAsync(string relativePath) => FileSystem.Current.OpenAppPackageFileAsync(relativePath);

    public async Task<bool> ExistsAsync(string relativePath)
    {
        try
        {
            await using var s = await FileSystem.Current.OpenAppPackageFileAsync(relativePath);
            return true;
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return false;
        }
    }

    private static bool IsNotFound(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException
#if ANDROID
        || ex is Java.IO.FileNotFoundException
#endif
        ;
}

/// <summary>
/// Extracts the bundled models once (first launch / app update) before any model is resolved. Release builds read
/// only bundled models; Debug builds also accept side-loaded models (developer convenience, see docs/MODELS.md).
/// </summary>
public sealed class BootstrappingModelProvider(IAppPaths paths, ILoggerFactory loggers) : IModelProvider
{
    private readonly Lazy<Task<FileSystemModelProvider>> _inner = new(async () =>
    {
        var logger = loggers.CreateLogger<BootstrappingModelProvider>();
        try
        {
            int copied = await BundledModelExtractor.ExtractAsync(new MauiAppPackageFiles(), "models", paths.BundledModelsDirectory, CancellationToken.None);
            logger.LogInformation("Bundled models ready ({Copied} files copied)", copied);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Extracting bundled models failed");
        }

        var roots = new List<string>();
#if DEBUG
        roots.Add(paths.SideloadModelsDirectory);
#endif
        roots.Add(paths.BundledModelsDirectory);
        return new FileSystemModelProvider(roots, Path.Combine(paths.CacheDirectory, "model-checksums.json"), loggers.CreateLogger<FileSystemModelProvider>());
    });

    public async Task<IReadOnlyList<ModelStatus>> GetStatusAsync(CancellationToken cancellationToken) =>
        await (await _inner.Value.ConfigureAwait(false)).GetStatusAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ResolvedModel?> ResolveAsync(ModelRole role, CancellationToken cancellationToken) =>
        await (await _inner.Value.ConfigureAwait(false)).ResolveAsync(role, cancellationToken).ConfigureAwait(false);

    public void ReportFailure(ModelRole role, ModelState state, string message)
    {
        if (_inner.Value.IsCompletedSuccessfully)
        {
            _inner.Value.Result.ReportFailure(role, state, message);
        }
    }
}
