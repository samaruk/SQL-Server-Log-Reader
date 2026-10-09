using System.Text.Encodings.Web;
using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Runtime;
using BonikPOS.VoiceSearch.Audio;
using BonikPOS.VoiceSearch.Audio.Benchmark;
using BonikPOS.VoiceSearch.Core.Benchmark;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Telemetry;
using BonikPOS.VoiceSearch.Infrastructure.Models;
using BonikPOS.VoiceSearch.MedicineSearch;
using BonikPOS.VoiceSearch.MedicineSearch.Catalog;

namespace BonikPOS.VoiceSearch.Evaluation.Pipeline;

/// <summary>
/// Desktop run of the same <see cref="PipelineBenchmark"/> the app runs on phones. Builds a temporary model root with the
/// real VAD and speaker encoder from models/ plus the given ASR export (and optional TSE model), which may be random-
/// weight COMPUTE-SHAPE models: they are labelled as test fixtures and only their timing is meaningful.
/// </summary>
public static class PipelineBenchCommand
{
    public static async Task<int> RunAsync(string solutionRoot, string asrConfig, string? tseModel, int repetitions, bool warmUp, bool parallel,
        bool realTime, int threads, int streamingThreads, bool spinning, string label, string? jsonOut, string? mdOut)
    {
        string root = Path.Combine(Path.GetTempPath(), "bonik-pipeline-bench-" + Environment.ProcessId);
        Directory.CreateDirectory(root);
        try
        {
            WriteManifest(root, solutionRoot, asrConfig, tseModel);
            var telemetry = new PerformanceTelemetry();
            using var runtime = new OnnxVoiceModelRuntime(new FileSystemModelProvider([root], null), telemetry, allowTestFixtures: true);
            var search = new MedicineSearchService(() => new DemoCatalogSource());
            await search.InitializeAsync(CancellationToken.None);
            var settings = new VoiceSearchSettings() with
            {
                Accelerator = AcceleratorPreference.CpuOnly,
                TargetSpeakerExtractionEnabled = tseModel is not null,
                InferenceThreads = threads,
                StreamingModelThreads = streamingThreads,
                InferenceThreadSpinning = spinning,
            };
            var bench = new PipelineBenchmark(runtime, search, new ProcessResourceSampler(), _ => Task.FromResult(settings));
            var clips = await LoadClipsAsync(Path.Combine(solutionRoot, "tests", "fixtures", "audio"));
            var progress = new Progress<string>(m => Console.Error.WriteLine(m));
            string loadAtStart = BonikPOS.VoiceSearch.Evaluation.Asr.AsrBenchmark.LoadAverage();
            var report = await bench.RunAsync(clips, new PipelineBenchmarkOptions
            {
                Repetitions = repetitions,
                RealTime = realTime,
                CompareAccelerators = false, // no mobile accelerator on a desktop
                WarmUp = warmUp,
                ParallelVerification = parallel,
                Label = label,
            }, progress, CancellationToken.None);
            report = report with
            {
                Notes = [.. report.Notes, $"Host load average (1/5/15 min) at start {loadAtStart}, at end {BonikPOS.VoiceSearch.Evaluation.Asr.AsrBenchmark.LoadAverage()}."],
            };
            string md = BenchmarkMarkdown.Render(report);
            Console.WriteLine(md);
            if (mdOut is not null)
            {
                await File.WriteAllTextAsync(mdOut, md);
            }

            if (jsonOut is not null)
            {
                await File.WriteAllTextAsync(jsonOut, JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                }));
            }

            return 0;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Synthetic TTS clips listed in tests/fixtures/audio/benchmark-clips.json (the set the app packages in Debug builds).</summary>
    public static Task<IReadOnlyList<BenchmarkClip>> LoadClipsAsync(string dir) =>
        BenchmarkClipManifest.ReadDirectoryAsync(dir, "benchmark-clips.json", CancellationToken.None);

    private static void WriteManifest(string root, string solutionRoot, string asrConfig, string? tseModel)
    {
        string Link(string source, string name)
        {
            string target = Path.Combine(root, name);
            File.CreateSymbolicLink(target, Path.GetFullPath(source));
            return name;
        }

        var bundled = JsonDocument.Parse(File.ReadAllText(Path.Combine(solutionRoot, "models", "models.json"))).RootElement;
        var models = new List<object>();
        foreach (var m in bundled.GetProperty("models").EnumerateArray())
        {
            var files = new Dictionary<string, string>();
            foreach (var f in m.GetProperty("files").EnumerateObject())
            {
                files[f.Name] = Link(Path.Combine(solutionRoot, "models", f.Value.GetString()!), Path.GetFileName(f.Value.GetString()!));
            }

            models.Add(new
            {
                id = m.GetProperty("id").GetString(),
                version = m.GetProperty("version").GetString(),
                role = m.GetProperty("role").GetString(),
                format = m.GetProperty("format").GetString(),
                files,
                license = m.GetProperty("license").GetString(),
                source = m.GetProperty("source").GetString(),
                options = m.TryGetProperty("options", out var o) ? o : default(JsonElement?),
            });
        }

        // ASR export: {prefix}-config.json names its encoder, decoder and tokens files.
        string prefix = Path.GetFileName(asrConfig)[..^"-config.json".Length];
        var (encoder, decoder, tokens) = BonikPOS.VoiceSearch.Evaluation.Asr.AsrBenchmark.ExportFiles(asrConfig);
        models.Add(new
        {
            id = "asr-benchmark-" + prefix,
            version = "bench",
            role = "SpeechRecognition",
            format = "bonikpos-whisper-onnx/1",
            files = new Dictionary<string, string>
            {
                ["config"] = Link(asrConfig, Path.GetFileName(asrConfig)),
                ["encoder"] = Link(encoder, Path.GetFileName(encoder)),
                ["decoder"] = Link(decoder, Path.GetFileName(decoder)),
                ["tokens"] = Link(tokens, Path.GetFileName(tokens)),
            },
            license = "n/a",
            source = "benchmark model given on the command line (may be random-weight; timing only)",
            isTestFixture = true,
        });
        if (tseModel is not null)
        {
            models.Add(new
            {
                id = "tse-benchmark",
                version = "bench",
                role = "TargetSpeakerExtraction",
                format = "streaming-tse-stft-onnx/1",
                files = new Dictionary<string, string> { ["model"] = Link(tseModel, Path.GetFileName(tseModel)) },
                license = "n/a",
                source = "benchmark model given on the command line (random weights; timing only)",
                isTestFixture = true,
                options = JsonDocument.Parse("""{"embedding_dim": 256, "required_encoder_id": "ge2e-resemblyzer"}""").RootElement,
            });
        }

        File.WriteAllText(Path.Combine(root, "models.json"), JsonSerializer.Serialize(new { format = "bonikpos-models/1", models },
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    }
}
