using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BonikPOS.VoiceSearch.Core.Benchmark;

/// <summary>A labelled audio clip (16 kHz mono) replayed through the pipeline by the benchmark.</summary>
/// <param name="IsOwner">The clip is spoken by the voice the benchmark enrols as owner.</param>
/// <param name="UseForEnrollment">The clip is used to build the benchmark's temporary owner reference (never searched).</param>
public sealed record BenchmarkClip(string Name, float[] Samples16k, bool IsOwner, bool UseForEnrollment);

public sealed record PipelineBenchmarkOptions
{
    public int Repetitions { get; init; } = 3;

    /// <summary>Replay audio at real-time speed (10 ms callbacks), as a microphone would deliver it.</summary>
    public bool RealTime { get; init; } = true;

    /// <summary>Run once with CPU only and once with the platform accelerator (XNNPACK / CoreML) when available.</summary>
    public bool CompareAccelerators { get; init; } = true;

    /// <summary>Run the model warm-up before the first clip (the app does this after loading models).</summary>
    public bool WarmUp { get; init; } = true;

    /// <summary>Overlap owner verification with speech recognition (the default pipeline behaviour).</summary>
    public bool ParallelVerification { get; init; } = true;

    public string Label { get; init; } = string.Empty;
}

/// <summary>One point-in-time reading of process and device resources. Values the platform does not expose are null.</summary>
/// <param name="BatteryCurrentMicroamps">
/// Android BatteryManager.BATTERY_PROPERTY_CURRENT_NOW as reported: negative while discharging per the Android
/// documentation, but sign and unit (some devices report mA) vary by manufacturer. Whole-device current, not the app's.
/// </param>
public sealed record ResourceSample(
    DateTimeOffset Timestamp,
    double? ProcessCpuMilliseconds,
    double ManagedMemoryMb,
    double? WorkingSetMb,
    double? BatteryLevel,
    string? BatteryState,
    string? ThermalState,
    double? BatteryCurrentMicroamps,
    double? BatteryTemperatureCelsius = null,
    double? BatteryVoltageMillivolts = null);

public interface IResourceSampler
{
    ResourceSample Sample();
}

/// <summary>Process CPU time and memory from System.Diagnostics; no battery or thermal data (desktop / fallback).</summary>
public sealed class ProcessResourceSampler : IResourceSampler
{
    public ResourceSample Sample()
    {
        double? cpu = null, ws = null;
        try
        {
            using var p = Process.GetCurrentProcess();
            cpu = p.TotalProcessorTime.TotalMilliseconds;
            ws = p.WorkingSet64 / (1024.0 * 1024.0);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or NotSupportedException)
        {
        }

        return new ResourceSample(DateTimeOffset.UtcNow, cpu, GC.GetTotalMemory(false) / (1024.0 * 1024.0), ws, null, null, null, null);
    }
}

public sealed record ModelUse(string Role, string? ModelId, string? ExecutionProvider, double? InitMilliseconds, bool Available);

/// <summary>Timings of one replayed clip. Times are milliseconds measured in the app process.</summary>
public sealed record ClipRun(
    string Clip,
    int Repetition,
    string Accelerator,
    string Outcome,
    double SpeechMs,
    double? VerifyMs,
    double? AsrMs,
    double? AsrFeatureMs,
    double? AsrEncoderMs,
    double? AsrDecoderMs,
    int? AsrTokens,
    double? SearchMs,
    double? EndOfSpeechToResultMs,
    double? LastSampleToResultMs,
    double? EndOfSpeechToOutcomeMs = null);

public sealed record BenchmarkMetric(string Name, long Count, double P50, double P95, double Max, double Mean);

public sealed record AcceleratorRun(
    string Accelerator,
    IReadOnlyList<ModelUse> Models,
    double WarmUpMs,
    IReadOnlyList<ClipRun> Clips,
    IReadOnlyList<BenchmarkMetric> Metrics,
    long FramesCaptured,
    long FramesDropped,
    long Discontinuities,
    long RecognitionQueueDrops,
    double WallSeconds,
    double? ProcessCpuPercentOfOneCore,
    double? ProcessCpuPercentOfDevice,
    double PeakManagedMb,
    double? PeakWorkingSetMb,
    ResourceSample Before,
    ResourceSample After,
    double? MeanBatteryCurrentMicroamps,
    double? MeanBatteryPowerMilliwatts = null,
    double? MaxBatteryTemperatureCelsius = null);

/// <summary>Result of a pipeline benchmark. Contains timings and resource readings only - no audio, no text.</summary>
public sealed record PipelineBenchmarkReport(
    string Format,
    string Label,
    DateTimeOffset StartedUtc,
    string Device,
    string Runtime,
    int LogicalProcessors,
    PipelineBenchmarkOptions Options,
    IReadOnlyList<AcceleratorRun> Runs,
    IReadOnlyList<string> Notes)
{
    public static string DescribeRuntime() =>
        $"{RuntimeInformation.OSDescription}; {RuntimeInformation.OSArchitecture}; .NET {Environment.Version}";
}

/// <summary>Replays clips through the full voice-search pipeline and measures latency, throughput and resources.</summary>
public interface IPipelineBenchmark
{
    Task<PipelineBenchmarkReport> RunAsync(
        IReadOnlyList<BenchmarkClip> clips, PipelineBenchmarkOptions options, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>Supplies benchmark clips (bundled synthetic clips in debug builds, or files the developer side-loaded).</summary>
public interface IBenchmarkClipSource
{
    Task<IReadOnlyList<BenchmarkClip>> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>JSON for benchmark reports (source-generated, so it works in trimmed / AOT app builds).</summary>
public static class BenchmarkJson
{
    public static string Serialize(PipelineBenchmarkReport report) =>
        System.Text.Json.JsonSerializer.Serialize(report, BenchmarkJsonContext.Default.PipelineBenchmarkReport);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[System.Text.Json.Serialization.JsonSerializable(typeof(PipelineBenchmarkReport))]
internal sealed partial class BenchmarkJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
