using System.Diagnostics;
using BonikPOS.VoiceSearch.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace BonikPOS.VoiceSearch.AI.Onnx;

/// <param name="Accelerator">Execution-provider preference.</param>
/// <param name="IntraOpThreads">Threads for models run once per utterance (speaker encoder, ASR); 0 = min(4, cores).</param>
/// <param name="AllowSpinning">Whether ONNX Runtime worker threads busy-wait for work between operators.</param>
/// <param name="StreamingThreads">Threads for the small models run every 10-32 ms (VAD, extraction); 0 = as <paramref name="IntraOpThreads"/>.</param>
public sealed record OnnxSessionPreferences(AcceleratorPreference Accelerator, int IntraOpThreads, bool AllowSpinning = false, int StreamingThreads = 1)
{
    public static OnnxSessionPreferences Default { get; } = new(AcceleratorPreference.Auto, 0);

    public int EffectiveThreads => IntraOpThreads > 0 ? IntraOpThreads : Math.Clamp(Environment.ProcessorCount, 1, 4);

    /// <summary>
    /// Preferences for streaming models. With one thread a Run executes on the calling (audio-processing) thread and
    /// no worker pool exists that could spin or be woken every frame.
    /// </summary>
    public OnnxSessionPreferences ForStreaming() => StreamingThreads > 0 ? this with { IntraOpThreads = StreamingThreads } : this;
}

/// <summary>A persistent session plus how it was created (shown on the diagnostics screen).</summary>
public sealed class LoadedSession(InferenceSession session, string executionProvider, double initMilliseconds) : IDisposable
{
    public InferenceSession Session { get; } = session;

    public string ExecutionProvider { get; } = executionProvider;

    public double InitMilliseconds { get; } = initMilliseconds;

    public void Dispose() => Session.Dispose();
}

/// <summary>
/// Creates long-lived ONNX Runtime sessions. CPU is the baseline; with <see cref="AcceleratorPreference.Auto"/> the
/// platform accelerator is attempted for models that benefit (XNNPACK on Android, CoreML on iOS) and the factory falls
/// back to CPU if the provider is missing or rejects the model.
/// </summary>
public static class OnnxSessionFactory
{
    static OnnxSessionFactory() => OnnxRuntimeTelemetry.Disable();

    public static LoadedSession Create(string modelPath, OnnxSessionPreferences preferences, bool allowAccelerator, ILogger? logger = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("Model file not found.", modelPath);
        }

        var sw = Stopwatch.StartNew();
        if (allowAccelerator && preferences.Accelerator == AcceleratorPreference.Auto)
        {
            string? provider = OperatingSystem.IsAndroid() ? "XNNPACK" : OperatingSystem.IsIOS() ? "CoreML" : null;
            if (provider is not null)
            {
                try
                {
                    using var options = BaseOptions(preferences, provider);
                    var session = new InferenceSession(modelPath, options);
                    return new LoadedSession(session, provider, sw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException)
                {
                    logger?.LogWarning(ex, "{Provider} unavailable for {Model}; using CPU", provider, Path.GetFileName(modelPath));
                }
            }
        }

        using var cpuOptions = BaseOptions(preferences, null);
        var cpu = new InferenceSession(modelPath, cpuOptions);
        return new LoadedSession(cpu, "CPU", sw.Elapsed.TotalMilliseconds);
    }

    private static SessionOptions BaseOptions(OnnxSessionPreferences preferences, string? provider)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = preferences.EffectiveThreads,
            EnableMemoryPattern = true,
            EnableCpuMemArena = true,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        try
        {
            // ORT's default lets idle workers busy-wait; between utterances and between 10-32 ms streaming calls that
            // only burns CPU (and battery). docs/BENCHMARKS.md has the measured effect.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", preferences.AllowSpinning ? "1" : "0");
            switch (provider)
            {
                case "XNNPACK":
                    // XNNPACK runs its own thread pool; ORT's pool is reduced to avoid oversubscription.
                    options.IntraOpNumThreads = 1;
                    options.AppendExecutionProvider("XNNPACK", new Dictionary<string, string>
                    {
                        ["intra_op_num_threads"] = preferences.EffectiveThreads.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
                    break;
                case "CoreML":
                    options.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                    {
                        ["ModelFormat"] = "MLProgram",
                        ["MLComputeUnits"] = "ALL",
                    });
                    break;
            }
        }
        catch
        {
            options.Dispose();
            throw;
        }

        return options;
    }
}
