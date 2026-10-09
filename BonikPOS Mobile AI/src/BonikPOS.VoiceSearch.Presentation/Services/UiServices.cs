namespace BonikPOS.VoiceSearch.Presentation.Services;

/// <summary>Route names registered in AppShell.</summary>
public static class Routes
{
    public const string Search = "//search";
    public const string History = "//history";
    public const string Settings = "//settings";
    public const string Welcome = "welcome";
    public const string MicrophoneTest = "microphone-test";
    public const string EnrollmentInstructions = "enrollment-instructions";
    public const string EnrollmentRecording = "enrollment-recording";
    public const string EnrollmentResult = "enrollment-result";
    public const string Ambiguity = "ambiguity";
    public const string NoMatch = "no-match";
    public const string VoiceSettings = "voice-settings";
    public const string Diagnostics = "diagnostics";
}

public interface INavigationService
{
    Task GoToAsync(string route);

    Task GoBackAsync();
}

public interface IDialogService
{
    Task AlertAsync(string title, string message, string accept = "OK");

    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}

public enum PermissionStatus
{
    Unknown,
    Granted,
    Denied,
    /// <summary>Denied and the system will not ask again; the owner must enable it in system settings.</summary>
    DeniedPermanently,
}

public interface IPermissionService
{
    Task<PermissionStatus> CheckMicrophoneAsync();

    Task<PermissionStatus> RequestMicrophoneAsync();

    void OpenAppSettings();
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public interface IFilePickerService
{
    /// <summary>Lets the owner pick a JSON product index; returns the path of a private copy, or null when cancelled.</summary>
    Task<string?> PickCatalogFileAsync();
}

public interface IAppInfoService
{
    string Version { get; }

    string Platform { get; }

    string DeviceModel { get; }
}

/// <summary>Battery, thermal and process measurements for the diagnostics screen.</summary>
public sealed record DeviceStatus(
    double? BatteryLevel,
    string BatteryState,
    string ThermalState,
    bool? EnergySaver,
    double ManagedMemoryMb,
    double? WorkingSetMb,
    double? ProcessCpuPercent);

public interface IDeviceStatusService
{
    DeviceStatus GetStatus();
}

/// <summary>Synchronous dispatcher for tests and for code already on the UI thread.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
