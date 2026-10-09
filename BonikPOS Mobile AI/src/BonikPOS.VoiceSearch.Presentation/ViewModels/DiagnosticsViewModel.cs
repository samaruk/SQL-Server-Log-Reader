using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

public sealed record DiagnosticRow(string Title, string Value, string? Detail = null, bool IsProblem = false);

/// <summary>Model and performance diagnostics. Shows numbers only: no audio and no recognised text.</summary>
public sealed partial class DiagnosticsViewModel(
    IModelProvider models,
    IVoiceModelRuntime runtime,
    IPerformanceTelemetry telemetry,
    ICatalogManager catalog,
    IMicrophoneCapture capture,
    SettingsService settings,
    IDeviceStatusService device,
    IAppInfoService appInfo,
    IClipboardService clipboard) : ViewModelBase
{
    public ObservableCollection<DiagnosticRow> ModelRows { get; } = [];

    public ObservableCollection<DiagnosticRow> PerformanceRows { get; } = [];

    public ObservableCollection<DiagnosticRow> DeviceRows { get; } = [];

    [ObservableProperty]
    public partial string? SelfTestSummary { get; set; }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await runtime.InitializeAsync(await settings.LoadAsync(CancellationToken.None), CancellationToken.None);
        await FillModelsAsync();
        FillPerformance();
        FillDevice();
    }, "Diagnostics failed");

    [RelayCommand]
    private Task ReloadModelsAsync() => RunAsync(async () =>
    {
        await runtime.ReloadAsync(await settings.LoadAsync(CancellationToken.None), CancellationToken.None);
        await FillModelsAsync();
        FillPerformance();
    }, "Reloading models failed");

    [RelayCommand]
    private void ResetTelemetry()
    {
        telemetry.Reset();
        FillPerformance();
    }

    /// <summary>Times each installed model on SYNTHETIC signals (not speech) to measure speed on this phone.</summary>
    [RelayCommand]
    private Task RunSelfTestAsync() => RunAsync(async () =>
    {
        await runtime.InitializeAsync(await settings.LoadAsync(CancellationToken.None), CancellationToken.None);
        var lines = new List<string> { "Synthetic-signal timing (measures speed only, not accuracy):" };
        var signal = Synthetic(16000 * 3);
        await Task.Run(async () =>
        {
            if (runtime.Status.VoiceActivity.Available)
            {
                using var vad = runtime.CreateVoiceActivityDetector();
                var chunk = new float[vad.ChunkSamples];
                var sw = Stopwatch.StartNew();
                int n = 0;
                for (int i = 0; i + chunk.Length <= signal.Length; i += chunk.Length, n++)
                {
                    signal.AsSpan(i, chunk.Length).CopyTo(chunk);
                    vad.Process(chunk);
                }

                lines.Add($"VAD: {sw.Elapsed.TotalMilliseconds / n:0.00} ms per 32 ms chunk");
                telemetry.Record(TelemetryMetrics.VadChunkMs, sw.Elapsed.TotalMilliseconds / n);
            }

            if (runtime.SpeakerEncoder is { } encoder)
            {
                encoder.EmbedUtterance(signal); // warm-up
                var sw = Stopwatch.StartNew();
                encoder.EmbedUtterance(signal);
                lines.Add($"Speaker embedding: {sw.Elapsed.TotalMilliseconds:0} ms for 3 s of audio");
                telemetry.Record(TelemetryMetrics.SpeakerEmbedMs, sw.Elapsed.TotalMilliseconds);
            }

            if (runtime.Recognizer.Info.IsAvailable)
            {
                var sw = Stopwatch.StartNew();
                var r = await runtime.Recognizer.RecognizeAsync(signal, new SpeechRecognitionOptions { MaxTokens = 16 }, CancellationToken.None);
                lines.Add($"Speech recognition: {sw.Elapsed.TotalMilliseconds:0} ms (encoder {r.EncoderTime.TotalMilliseconds:0} ms, decoder {r.DecoderTime.TotalMilliseconds:0} ms)");
            }
        });
        SelfTestSummary = string.Join("\n", lines);
        FillPerformance();
    }, "Self-test failed");

    [RelayCommand]
    private Task CopyReportAsync() => RunAsync(async () =>
    {
        await clipboard.SetTextAsync(BuildReport());
    }, "Could not copy the report");

    private async Task FillModelsAsync()
    {
        ModelRows.Clear();
        var status = runtime.Status;
        foreach (var m in await models.GetStatusAsync(CancellationToken.None))
        {
            var component = m.Role switch
            {
                ModelRole.VoiceActivity => status.VoiceActivity,
                ModelRole.SpeakerEncoder => status.SpeakerEncoder,
                ModelRole.TargetSpeakerExtraction => status.TargetSpeakerExtraction,
                _ => status.SpeechRecognition,
            };
            string value = m.State == ModelState.Ready && component.Available
                ? $"Ready · {component.ExecutionProvider} · init {component.InitMilliseconds:0} ms"
                : m.State == ModelState.Ready ? "Not loaded" : m.State.ToString();
            string detail = $"{m.ModelId ?? "none"} {m.Version} · licence {m.License ?? "-"} · {m.Message}" +
                            (component.Available ? string.Empty : $" · {component.Description}");
            ModelRows.Add(new DiagnosticRow(RoleName(m.Role), value, detail, !(m.State == ModelState.Ready && component.Available)));
        }

        foreach (var message in status.Messages)
        {
            ModelRows.Add(new DiagnosticRow("Message", message, null, true));
        }
    }

    private void FillPerformance()
    {
        PerformanceRows.Clear();
        var snapshot = telemetry.Snapshot();
        foreach (var m in snapshot.Metrics)
        {
            PerformanceRows.Add(new DiagnosticRow(m.Name, $"p50 {m.P50:0.0} · p95 {m.P95:0.0} · max {m.Max:0.0}", $"{m.Count} samples, last {m.Last:0.0}"));
        }

        foreach (var (name, value) in snapshot.Counters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            bool problem = name is TelemetryMetrics.FramesDropped or TelemetryMetrics.RecognitionQueueDrops && value > 0;
            PerformanceRows.Add(new DiagnosticRow(name, value.ToString(CultureInfo.InvariantCulture), null, problem));
        }

        var q = capture.Frames.GetStatistics();
        PerformanceRows.Add(new DiagnosticRow("audio.queue", $"depth {q.Depth}/{q.Capacity}, max {q.MaxDepth}, dropped {q.FramesDropped}",
            capture.ActiveConfiguration is { } c ? $"{c.DeviceSampleRate} Hz, {c.CallbackFrames}-sample callbacks, {c.InputPath}" : null, q.FramesDropped > 0));
    }

    private void FillDevice()
    {
        DeviceRows.Clear();
        var d = device.GetStatus();
        DeviceRows.Add(new DiagnosticRow("App", $"{appInfo.Version} on {appInfo.Platform}", appInfo.DeviceModel));
        DeviceRows.Add(new DiagnosticRow("Battery", d.BatteryLevel is double b ? $"{b:P0} ({d.BatteryState})" : d.BatteryState,
            d.EnergySaver == true ? "Energy saver on" : null));
        DeviceRows.Add(new DiagnosticRow("Thermal state", d.ThermalState, null, d.ThermalState is "Serious" or "Critical" or "Severe" or "Emergency" or "Shutdown"));
        DeviceRows.Add(new DiagnosticRow("Memory", $"managed {d.ManagedMemoryMb:0} MB" + (d.WorkingSetMb is double w ? $", process {w:0} MB" : string.Empty)));
        if (d.ProcessCpuPercent is double cpu)
        {
            DeviceRows.Add(new DiagnosticRow("Process CPU", $"{cpu:0}% (since last refresh)"));
            telemetry.Record(TelemetryMetrics.CpuPercent, cpu);
        }

        var i = catalog.Info;
        DeviceRows.Add(new DiagnosticRow("Catalog", i.IsLoaded ? $"{i.ProductCount} products" : "not loaded", i.SourceDescription, !i.IsLoaded || i.IsDemoData));
    }

    private string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BonikPOS Voice Search diagnostics {DateTimeOffset.Now:O}");
        sb.AppendLine($"App {appInfo.Version} · {appInfo.Platform} · {appInfo.DeviceModel}");
        foreach (var section in new[] { ("Models", ModelRows), ("Performance", PerformanceRows), ("Device", DeviceRows) })
        {
            sb.AppendLine($"[{section.Item1}]");
            foreach (var r in section.Item2)
            {
                sb.AppendLine($"{r.Title}: {r.Value}{(r.Detail is null ? string.Empty : " | " + r.Detail)}");
            }
        }

        if (SelfTestSummary is not null)
        {
            sb.AppendLine("[Self-test]").AppendLine(SelfTestSummary);
        }

        sb.AppendLine("(This report contains no audio and no recognised text.)");
        return sb.ToString();
    }

    private static string RoleName(ModelRole role) => role switch
    {
        ModelRole.VoiceActivity => "Voice activity detection",
        ModelRole.SpeakerEncoder => "Speaker encoder (owner verification)",
        ModelRole.TargetSpeakerExtraction => "Target-speaker extraction",
        _ => "Speech recognition",
    };

    private static float[] Synthetic(int samples)
    {
        var rng = new Random(1);
        var s = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = i / 16000.0;
            s[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 140 * t) * (1 + Math.Sin(2 * Math.PI * 3 * t)) + 0.01 * (rng.NextDouble() - 0.5));
        }

        return s;
    }
}
