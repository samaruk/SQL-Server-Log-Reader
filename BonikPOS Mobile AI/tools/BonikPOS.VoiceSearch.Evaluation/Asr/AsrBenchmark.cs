using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.AI.Recognition.Whisper;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Evaluation.Tse;
using Microsoft.ML.OnnxRuntime;

namespace BonikPOS.VoiceSearch.Evaluation.Asr;

/// <summary>
/// Whisper compute cost on THIS machine with the app's ONNX Runtime build and session options: session creation,
/// log-mel, encoder, the first two decoder steps and single-token decoder steps. Decoder steps are teacher-forced
/// (<see cref="WhisperOnnxRecognizer.MeasureCompute"/>), so random-weight compute-shape exports do the same work as
/// trained checkpoints of the same dimensions. Each (model, threads) pair runs in its own process so the peak working
/// set belongs to that model alone. Timing and memory only; recognition quality is not measured.
/// </summary>
public static class AsrBenchmark
{
    public sealed record Row(
        string Config, string ModelId, string Quantization, bool RandomWeights, double WindowSeconds, int Threads,
        double EncoderFileMb, double DecoderFileMb, string EncoderSha256Prefix, double InitMs, int Repetitions, int Tokens,
        double FeatureP50, double EncoderP50, double EncoderP95, double StartStepP50, double PromptStepP50,
        double TokenP50, double TokenP95, double TotalP50, double TotalP95, double PeakWorkingSetMb);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> configs, IReadOnlyList<int> threads, int repetitions, int tokens, double seconds, string? jsonOut, string? mdOut)
    {
        string loadAtStart = LoadAverage();
        var rows = new List<Row>();
        foreach (var config in configs)
        {
            foreach (int t in threads)
            {
                Console.Error.WriteLine($"asr-bench: {Path.GetFileName(config)} threads={t}");
                rows.Add(await RunChildAsync(config, t, repetitions, tokens, seconds));
            }
        }

        string md = Markdown(rows, repetitions, tokens, seconds, loadAtStart, LoadAverage());
        Console.WriteLine(md);
        if (mdOut is not null)
        {
            await File.WriteAllTextAsync(mdOut, md);
        }

        if (jsonOut is not null)
        {
            await File.WriteAllTextAsync(jsonOut, JsonSerializer.Serialize(new
            {
                format = "bonikpos-asr-bench/1",
                machine = TseBenchmark.Machine(),
                ort = typeof(InferenceSession).Assembly.GetName().Version?.ToString(),
                generated_utc = DateTimeOffset.UtcNow,
                audio_seconds = seconds,
                load_average_start = loadAtStart,
                rows,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        return 0;
    }

    /// <summary>Child-process entry: measures one model with one thread count and writes a JSON row.</summary>
    public static int RunOne(string configPath, int threads, int repetitions, int tokens, double seconds, string rowOut)
    {
        OnnxRuntimeTelemetry.Disable();
        var model = Resolve(configPath);
        var config = WhisperModelConfig.Load(configPath);
        var load = Stopwatch.StartNew();
        using var asr = WhisperOnnxRecognizer.Load(model, new OnnxSessionPreferences(AcceleratorPreference.CpuOnly, threads));
        double initMs = load.Elapsed.TotalMilliseconds;
        var audio = SyntheticSpeechLikeAudio(seconds);
        asr.MeasureCompute(audio, tokens); // warm-up run, discarded
        var runs = new List<WhisperComputeTiming>(repetitions);
        for (int i = 0; i < repetitions; i++)
        {
            runs.Add(asr.MeasureCompute(audio, tokens));
        }

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var perToken = runs.SelectMany(r => r.TokenStepMs).ToList();
        string encoderPath = model.PathOf("encoder");
        var row = new Row(
            Path.GetFileName(configPath), config.ModelId, config.Quantization, config.IsRandomTestModel,
            config.AudioContext * 2 / 100.0, threads,
            new FileInfo(encoderPath).Length / 1048576.0, new FileInfo(model.PathOf("decoder")).Length / 1048576.0,
            Sha256Prefix(encoderPath), initMs, repetitions, tokens,
            P(runs.Select(r => r.FeatureMs), 50), P(runs.Select(r => r.EncoderMs), 50), P(runs.Select(r => r.EncoderMs), 95),
            P(runs.Select(r => r.StartStepMs), 50), P(runs.Select(r => r.PromptStepMs), 50),
            P(perToken, 50), P(perToken, 95), P(runs.Select(r => r.TotalMs), 50), P(runs.Select(r => r.TotalMs), 95),
            process.PeakWorkingSet64 / 1048576.0);
        File.WriteAllText(rowOut, JsonSerializer.Serialize(row));
        return 0;
    }

    private static async Task<Row> RunChildAsync(string config, int threads, int repetitions, int tokens, double seconds)
    {
        string rowFile = Path.Combine(Path.GetTempPath(), $"bonik-asr-bench-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        {
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        }

        foreach (var a in new[]
                 {
                     "asr-bench-one", "--config", Path.GetFullPath(config), "--threads", threads.ToString(CultureInfo.InvariantCulture),
                     "--repetitions", repetitions.ToString(CultureInfo.InvariantCulture), "--tokens", tokens.ToString(CultureInfo.InvariantCulture),
                     "--seconds", seconds.ToString(CultureInfo.InvariantCulture), "--row-out", rowFile,
                 })
        {
            start.ArgumentList.Add(a);
        }

        try
        {
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the benchmark process.");
            await child.WaitForExitAsync();
            if (child.ExitCode != 0 || !File.Exists(rowFile))
            {
                throw new InvalidOperationException($"asr-bench for {config} (threads {threads}) failed with exit code {child.ExitCode}.");
            }

            return JsonSerializer.Deserialize<Row>(await File.ReadAllTextAsync(rowFile))!;
        }
        finally
        {
            File.Delete(rowFile);
        }
    }

    /// <summary>An export ({prefix}-config.json naming its encoder, decoder and tokens files) as a resolved model.</summary>
    private static ResolvedModel Resolve(string configPath)
    {
        var (encoder, decoder, tokens) = ExportFiles(configPath);
        var files = new Dictionary<string, string>
        {
            ["config"] = Path.GetFullPath(configPath),
            ["encoder"] = encoder,
            ["decoder"] = decoder,
            ["tokens"] = tokens,
        };
        var descriptor = new ModelDescriptor
        {
            Id = Path.GetFileName(configPath)[..^"-config.json".Length],
            Version = "bench",
            Role = ModelRole.SpeechRecognition,
            Format = WhisperModelConfig.FormatId,
            Files = files,
            IsTestFixture = true,
        };
        return new ResolvedModel(descriptor, files);
    }

    /// <summary>
    /// Full paths of the files an export_whisper_onnx.py config names under "files" (int8 exports name the
    /// *.int8.onnx graphs there; the float graphs next to them are intermediate files).
    /// </summary>
    internal static (string Encoder, string Decoder, string Tokens) ExportFiles(string configPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        using var json = JsonDocument.Parse(File.ReadAllText(configPath));
        var files = json.RootElement.GetProperty("files");
        string Get(string key) => Path.Combine(dir, files.GetProperty(key).GetString()!);
        return (Get("encoder"), Get("decoder"), Get("tokens"));
    }

    /// <summary>Deterministic voiced-like signal (harmonics with a moving pitch plus noise). Content does not change the cost.</summary>
    private static float[] SyntheticSpeechLikeAudio(double seconds)
    {
        var rng = new Random(20261009);
        var x = new float[(int)(seconds * 16000)];
        double phase = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / 16000.0;
            double f0 = 140 + 30 * Math.Sin(2 * Math.PI * 1.5 * t);
            phase += 2 * Math.PI * f0 / 16000;
            double v = 0;
            for (int h = 1; h <= 8; h++)
            {
                v += Math.Sin(h * phase) / h;
            }

            x[i] = (float)(0.1 * v * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * t)) + 0.005 * (rng.NextDouble() - 0.5));
        }

        return x;
    }

    private static double P(IEnumerable<double> values, int percentile)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static string Sha256Prefix(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
    }

    /// <summary>1/5/15-minute load averages on Linux (other processes competing for the CPU make timings noisier).</summary>
    internal static string LoadAverage() =>
        File.Exists("/proc/loadavg") ? string.Join(' ', File.ReadAllText("/proc/loadavg").Split(' ').Take(3)) : "n/a";

    private static string Markdown(IReadOnlyList<Row> rows, int repetitions, int tokens, double seconds, string loadStart, string loadEnd)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Whisper ONNX compute benchmark (measured)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Machine: {TseBenchmark.Machine()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- ONNX Runtime: {typeof(InferenceSession).Assembly.GetName().Version}, CPU execution provider, app session options");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC; load average at start {loadStart}, at end {loadEnd}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- {repetitions} measured runs after one discarded warm-up run; {seconds:0.#} s synthetic audio; {tokens} teacher-forced tokens per run");
        sb.AppendLine("- Random-weight exports (marked *random*) have the published Whisper dimensions; their output is meaningless but the arithmetic per step is the same as a trained checkpoint of that shape.");
        sb.AppendLine("- Peak RSS is the whole benchmark process (runtime + one model), measured in a separate process per row.");
        sb.AppendLine();
        sb.AppendLine("| Model | Quant | Window s | Threads | Encoder/decoder MB | Init ms | Log-mel p50 | Encoder p50 / p95 | SOT step p50 | Prompt step p50 | Per token p50 / p95 | Total p50 / p95 | Peak RSS MB |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            string name = r.Config[..^"-config.json".Length] + (r.RandomWeights ? " *random*" : string.Empty);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {name} | {r.Quantization} | {r.WindowSeconds:0.#} | {r.Threads} | {r.EncoderFileMb:0} / {r.DecoderFileMb:0} | {r.InitMs:0} | {r.FeatureP50:0.0} | {r.EncoderP50:0} / {r.EncoderP95:0} | {r.StartStepP50:0.0} | {r.PromptStepP50:0.0} | {r.TokenP50:0.0} / {r.TokenP95:0.0} | {r.TotalP50:0} / {r.TotalP95:0} | {r.PeakWorkingSetMb:0} |");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Total = log-mel + encoder + SOT step + prompt step + {tokens} token steps of one run (one recognition of a {tokens}-token transcript without a second-language decode).");
        return sb.ToString();
    }
}
