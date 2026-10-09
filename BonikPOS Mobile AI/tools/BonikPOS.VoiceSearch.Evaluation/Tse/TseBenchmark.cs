using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Extraction;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.Evaluation.Tse;

/// <summary>
/// Measures the compute cost of streaming STFT-domain enhancement / extraction models on THIS machine with the same
/// ONNX Runtime build and session options as the app: (1) model only, one 16 ms frame per call with recurrent state
/// carried over; (2) for models with a speaker-embedding input, the full C# adapter (STFT + model + inverse STFT) fed in
/// 10 ms pieces as the live pipeline does. Inputs are synthetic; quality is not measured.
/// </summary>
public static class TseBenchmark
{
    public sealed record Row(
        string Model, string Sha256Prefix, long Bytes, string Mode, int Threads, int Frames,
        double MeanMs, double P50Ms, double P95Ms, double MaxMs, double RealTimeFactor, string Notes);

    public static int Run(IReadOnlyList<string> modelPaths, int seconds, IReadOnlyList<int> threads, string? jsonOut, string? mdOut)
    {
        OnnxRuntimeTelemetry.Disable();
        var rows = new List<Row>();
        foreach (var path in modelPaths)
        {
            foreach (int t in threads)
            {
                rows.Add(ModelOnly(path, seconds, t));
                if (HasEmbeddingInput(path))
                {
                    rows.Add(Adapter(path, seconds, t));
                }
            }
        }

        string md = Markdown(rows, seconds);
        Console.WriteLine(md);
        if (mdOut is not null)
        {
            File.WriteAllText(mdOut, md);
        }

        if (jsonOut is not null)
        {
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(new
            {
                format = "bonikpos-tse-bench/1",
                machine = Machine(),
                ort = typeof(InferenceSession).Assembly.GetName().Version?.ToString(),
                generated_utc = DateTimeOffset.UtcNow,
                rows,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        return 0;
    }

    public static string Machine() =>
        $"{RuntimeInformation.OSDescription}; {RuntimeInformation.OSArchitecture}; {Environment.ProcessorCount} logical CPUs; .NET {Environment.Version}";

    private static bool HasEmbeddingInput(string path)
    {
        using var s = new InferenceSession(path);
        return s.InputMetadata.ContainsKey("spk_emb");
    }

    /// <summary>Model only: one frame per Run with states fed back; random spectra.</summary>
    private static Row ModelOnly(string path, int seconds, int threads)
    {
        using var loaded = OnnxSessionFactory.Create(path, new OnnxSessionPreferences(AcceleratorPreference.CpuOnly, threads), allowAccelerator: false);
        var s = loaded.Session;
        var rng = new Random(5);
        var inputs = new List<(string Name, float[] Data, long[] Shape)>();
        foreach (var (name, meta) in s.InputMetadata)
        {
            long[] shape = [.. meta.Dimensions.Select(d => (long)Math.Max(d, 1))];
            var data = new float[shape.Aggregate(1L, (a, b) => a * b)];
            if (name == "spk_emb")
            {
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (float)rng.NextDouble();
                }
            }

            inputs.Add((name, data, shape));
        }

        var outputs = s.OutputMetadata.Select(p => (p.Key, Data: new float[p.Value.Dimensions.Aggregate(1L, (a, b) => a * Math.Max(b, 1))], Shape: p.Value.Dimensions.Select(d => (long)Math.Max(d, 1)).ToArray())).ToList();
        var inValues = inputs.Select(i => OrtValue.CreateTensorValueFromMemory(i.Data, i.Shape)).ToArray();
        var outValues = outputs.Select(o => OrtValue.CreateTensorValueFromMemory(o.Data, o.Shape)).ToArray();
        var inNames = inputs.Select(i => i.Name).ToArray();
        var outNames = outputs.Select(o => o.Key).ToArray();
        var feedback = inputs.Select((inp, idx) => (idx, outIdx: outputs.FindIndex(o => o.Key == inp.Name + "_out"))).Where(p => p.outIdx >= 0).ToList();
        var mix = inputs.First(i => i.Name == "mix").Data;
        using var runOptions = new RunOptions();
        int frames = seconds * 16000 / 256;
        var times = new double[frames];
        try
        {
            for (int f = -50; f < frames; f++)
            {
                for (int i = 0; i < mix.Length; i++)
                {
                    mix[i] = (float)(rng.NextDouble() - 0.5);
                }

                long t0 = Stopwatch.GetTimestamp();
                s.Run(runOptions, inNames, inValues, outNames, outValues);
                foreach (var (idx, outIdx) in feedback)
                {
                    outputs[outIdx].Data.CopyTo(inputs[idx].Data, 0);
                }

                double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (f >= 0)
                {
                    times[f] = ms;
                }
            }
        }
        finally
        {
            foreach (var v in inValues.Concat(outValues))
            {
                v.Dispose();
            }
        }

        return Summarize(path, "model only (1 frame / call)", threads, times, "random spectra; states carried over");
    }

    /// <summary>Full adapter: STFT + model + inverse STFT, fed 160-sample (10 ms) pieces like the pipeline.</summary>
    private static Row Adapter(string path, int seconds, int threads)
    {
        int dim;
        using (var probe = new InferenceSession(path))
        {
            dim = probe.InputMetadata["spk_emb"].Dimensions[1];
        }

        using var optionsDoc = JsonDocument.Parse($$"""{"embedding_dim": {{dim}}, "required_encoder_id": "benchmark-encoder"}""");
        var descriptor = new ModelDescriptor
        {
            Id = Path.GetFileNameWithoutExtension(path),
            Version = "bench",
            Role = ModelRole.TargetSpeakerExtraction,
            Format = StftStreamingTseModel.FormatId,
            Files = new Dictionary<string, string> { ["model"] = Path.GetFileName(path) },
            IsTestFixture = true,
            Options = optionsDoc.RootElement.Clone(),
        };
        using var model = StftStreamingTseModel.Load(new ResolvedModel(descriptor, new Dictionary<string, string> { ["model"] = path }),
            new OnnxSessionPreferences(AcceleratorPreference.CpuOnly, threads));
        using var extractor = (StftStreamingTargetSpeakerExtractor)model.CreateExtractor(null);
        var rng = new Random(9);
        var vector = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            vector[i] = (float)rng.NextDouble();
        }

        extractor.SetTarget(new SpeakerEmbedding("benchmark-encoder", "bench", vector));
        var input = new float[160];
        var output = new float[160];
        int calls = seconds * 100;
        var frameCallTimes = new List<double>();
        for (int c = -100; c < calls; c++)
        {
            for (int i = 0; i < input.Length; i++)
            {
                input[i] = (float)(0.05 * Math.Sin(2 * Math.PI * 220 * ((c + 100) * 160 + i) / 16000.0) + 0.01 * (rng.NextDouble() - 0.5));
            }

            long before = extractor.FramesProcessed;
            long t0 = Stopwatch.GetTimestamp();
            extractor.Process(input, output);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (c >= 0 && extractor.FramesProcessed > before)
            {
                frameCallTimes.Add(ms);
            }
        }

        if (!extractor.Status.IsActive)
        {
            throw new InvalidOperationException("Extractor did not become active: " + extractor.Status.Message);
        }

        return Summarize(path, "C# adapter (STFT+model+iSTFT, 10 ms pieces)", threads, [.. frameCallTimes],
            $"per call that ran a frame; latency {extractor.Status.AlgorithmicLatencySamples} samples");
    }

    private static Row Summarize(string path, string mode, int threads, double[] times, string notes)
    {
        var sorted = times.Order().ToArray();
        double mean = times.Average();
        using var f = File.OpenRead(path);
        string sha = Convert.ToHexStringLower(SHA256.HashData(f))[..12];
        return new Row(Path.GetFileName(path), sha, new FileInfo(path).Length, mode, threads, times.Length,
            mean, Search.SearchEvaluator.Percentile(sorted, 0.5), Search.SearchEvaluator.Percentile(sorted, 0.95), sorted[^1],
            mean / 16.0, notes);
    }

    private static string Markdown(IReadOnlyList<Row> rows, int seconds)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"Measured on {Machine()}, ONNX Runtime {typeof(InferenceSession).Assembly.GetName().Version}, CPU execution provider, {seconds} s of synthetic input per row (after warm-up). One frame = 256 new samples = 16 ms of audio; real-time factor = mean time per frame / 16 ms.");
        sb.AppendLine();
        sb.AppendLine("| Model | SHA-256 | Size | Mode | Threads | Frames | Mean ms | p50 ms | p95 ms | Max ms | RTF |");
        sb.AppendLine("|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            sb.AppendLine(inv, $"| {r.Model} | `{r.Sha256Prefix}` | {r.Bytes / 1024.0:0} KB | {r.Mode} | {r.Threads} | {r.Frames} | {r.MeanMs:0.000} | {r.P50Ms:0.000} | {r.P95Ms:0.000} | {r.MaxMs:0.000} | {r.RealTimeFactor:0.0000} |");
        }

        return sb.ToString();
    }
}
