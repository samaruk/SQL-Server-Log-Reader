using BonikPOS.VoiceSearch.Audio.Benchmark;
using BonikPOS.VoiceSearch.Core.Benchmark;
using BonikPOS.VoiceSearch.Infrastructure.Storage;

namespace BonikPOS.VoiceSearch.App.Services;

/// <summary>
/// Process CPU and memory plus battery and thermal readings for the pipeline benchmark.
/// Android: BatteryManager (charge, instantaneous current) and the sticky ACTION_BATTERY_CHANGED broadcast (status,
/// temperature, voltage), none of which needs a permission, and PowerManager thermal status (Android 10+).
/// iOS: UIDevice battery level and state (read on the main thread) and NSProcessInfo thermal state; iOS has no public
/// battery-current API.
/// </summary>
public sealed class MauiResourceSampler : IResourceSampler
{
    private readonly ProcessResourceSampler _process = new();
#if IOS
    private readonly Lock _gate = new();
    private double? _batteryLevel;
    private string? _batteryState;
    private long _lastBatteryRefresh;
#endif

    public ResourceSample Sample()
    {
        var sample = _process.Sample();
#if ANDROID
        return SampleAndroid(sample);
#elif IOS
        return SampleIos(sample);
#else
        return sample;
#endif
    }

#if ANDROID
    private static ResourceSample SampleAndroid(ResourceSample sample)
    {
        var context = Android.App.Application.Context;
        double? level = null, current = null, temperature = null, voltage = null;
        string? state = null, thermal = null;
        try
        {
            if (context.GetSystemService(Android.Content.Context.BatteryService) is Android.OS.BatteryManager battery)
            {
                int capacity = battery.GetIntProperty((int)Android.OS.BatteryProperty.Capacity);
                level = capacity is >= 0 and <= 100 ? capacity / 100.0 : null;
                int now = battery.GetIntProperty((int)Android.OS.BatteryProperty.CurrentNow);
                current = now is int.MinValue or 0 ? null : now; // MIN_VALUE (or 0 on some devices) = not supported
            }

            using var filter = new Android.Content.IntentFilter(Android.Content.Intent.ActionBatteryChanged);
            using var intent = context.RegisterReceiver(null, filter); // sticky broadcast: no receiver is registered
            if (intent is not null)
            {
                int tenths = intent.GetIntExtra(Android.OS.BatteryManager.ExtraTemperature, int.MinValue);
                temperature = tenths == int.MinValue ? null : tenths / 10.0;
                int millivolts = intent.GetIntExtra(Android.OS.BatteryManager.ExtraVoltage, 0);
                voltage = millivolts > 0 ? millivolts : null;
                var status = (Android.OS.BatteryStatus)intent.GetIntExtra(Android.OS.BatteryManager.ExtraStatus, (int)Android.OS.BatteryStatus.Unknown);
                int plugged = intent.GetIntExtra(Android.OS.BatteryManager.ExtraPlugged, 0);
                state = plugged == 0 ? $"{status}, unplugged" : $"{status}, plugged";
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(29)
                && context.GetSystemService(Android.Content.Context.PowerService) is Android.OS.PowerManager power)
            {
                thermal = power.CurrentThermalStatus.ToString();
            }
        }
        catch (Java.Lang.Exception)
        {
            // A vendor build without one of these services: the values stay unavailable.
        }

        return sample with
        {
            BatteryLevel = level,
            BatteryState = state,
            ThermalState = thermal ?? "unavailable (Android 10+ required)",
            BatteryCurrentMicroamps = current,
            BatteryTemperatureCelsius = temperature,
            BatteryVoltageMillivolts = voltage,
        };
    }
#endif

#if IOS
    private ResourceSample SampleIos(ResourceSample sample)
    {
        // UIDevice belongs to UIKit: read it on the main thread and serve the last reading to the sampling timer.
        long now = Environment.TickCount64;
        if (MainThread.IsMainThread)
        {
            RefreshBattery();
        }
        else if (now - Interlocked.Read(ref _lastBatteryRefresh) > 2000)
        {
            Interlocked.Exchange(ref _lastBatteryRefresh, now);
            MainThread.BeginInvokeOnMainThread(RefreshBattery);
        }

        lock (_gate)
        {
            return sample with
            {
                BatteryLevel = _batteryLevel,
                BatteryState = _batteryState,
                ThermalState = Foundation.NSProcessInfo.ProcessInfo.ThermalState.ToString(),
            };
        }
    }

    private void RefreshBattery()
    {
        var device = UIKit.UIDevice.CurrentDevice;
        device.BatteryMonitoringEnabled = true;
        float level = device.BatteryLevel; // -1 when unknown (simulator)
        string state = device.BatteryState.ToString();
        lock (_gate)
        {
            _batteryLevel = level >= 0 ? level : null;
            _batteryState = state;
        }

        Interlocked.Exchange(ref _lastBatteryRefresh, Environment.TickCount64);
    }
#endif
}

/// <summary>
/// Benchmark clips (format: <see cref="BenchmarkClipManifest"/>): clips.json plus 16 kHz WAV files that a developer copied
/// into the "benchmark" folder next to the side-load models folder, or else - in Debug builds only - the synthetic TTS
/// clips packaged from tests/fixtures/audio. Clips are replayed from memory; nothing is stored or uploaded.
/// </summary>
public sealed class MauiBenchmarkClipSource(IAppPaths paths) : IBenchmarkClipSource
{
    private const string Folder = "benchmark";

    public string SideloadFolder => Path.Combine(Path.GetDirectoryName(paths.SideloadModelsDirectory) ?? paths.SideloadModelsDirectory, Folder);

    public async Task<IReadOnlyList<BenchmarkClip>> LoadAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(SideloadFolder, "clips.json")))
        {
            return await BenchmarkClipManifest.ReadDirectoryAsync(SideloadFolder, "clips.json", cancellationToken).ConfigureAwait(false);
        }

        Stream manifest;
        try
        {
            manifest = await FileSystem.Current.OpenAppPackageFileAsync($"{Folder}/clips.json").ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return []; // Release build without side-loaded clips
        }

        await using (manifest.ConfigureAwait(false))
        {
            return await BenchmarkClipManifest.ReadAsync(manifest, file => FileSystem.Current.OpenAppPackageFileAsync($"{Folder}/{file}"), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
