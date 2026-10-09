using System.Globalization;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Evaluation.Search;
using BonikPOS.VoiceSearch.Evaluation.Synthetic;
using BonikPOS.VoiceSearch.MedicineSearch;
using BonikPOS.VoiceSearch.MedicineSearch.Catalog;

Console.OutputEncoding = System.Text.Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        BonikPOS Voice Search evaluation harness

          search        --cases <file.jsonl> [--catalog demo|<products.json>] [--label <text>] [--out <report.json>] [--md <report.md>]
                        [--min-candidate x] [--confident x] [--margin x] [--min-confirm x] [--min-fuzzy-confirm x] [--all-cases]
          make-catalog  --out <products.json> [--count 20000] [--seed 20261009]
          make-cases    --catalog <products.json> --out <cases.jsonl> [--count 2000] [--seed 7] [--negatives 500] [--phrases A|B|C]
          tse-bench     --models a.onnx,b.onnx [--seconds 10] [--threads 1,2] [--out r.json] [--md r.md]
          asr-bench     --configs a-config.json,b-config.json [--threads 1,2,4] [--repetitions 5] [--tokens 12] [--seconds 3]
                        [--out r.json] [--md r.md]
          pipeline-bench --asr-config <prefix-config.json> [--tse model.onnx] [--repetitions 3] [--no-warmup] [--sequential] [--fast]
                        [--threads 0] [--streaming-threads 1] [--spinning] [--solution-root .] [--label text] [--out r.json] [--md r.md]

        Reports contain only numbers measured in the run. Datasets authored by developers or generated synthetically are
        regression checks, not estimates of accuracy in a real pharmacy (see docs/SEARCH_EVALUATION.md).
        """);
    return 0;
}

var opts = Options.Parse(args.Skip(1));
switch (args[0])
{
    case "search":
        return await RunSearchAsync(opts);
    case "make-catalog":
    {
        int count = opts.Int("count", 20000);
        int seed = opts.Int("seed", 20261009);
        var products = RealisticSyntheticCatalog.Generate(count, seed);
        RealisticSyntheticCatalog.Write(opts.Required("out"), products, seed);
        Console.WriteLine($"Wrote {products.Count} SYNTHETIC products (seed {seed}) to {opts.Required("out")}");
        return 0;
    }

    case "make-cases":
    {
        var snapshot = await JsonCatalogSource.FromFile(opts.Required("catalog")).LoadAsync(CancellationToken.None);
        var cases = SyntheticCaseGenerator.Generate(snapshot.Products, opts.Int("count", 2000), opts.Int("negatives", 500), opts.Int("seed", 7),
            (opts.Get("phrases") ?? "A")[0]);
        SyntheticCaseGenerator.Write(opts.Required("out"), cases);
        Console.WriteLine($"Wrote {cases.Count} generated cases to {opts.Required("out")}");
        return 0;
    }

    case "pipeline-bench":
        return await BonikPOS.VoiceSearch.Evaluation.Pipeline.PipelineBenchCommand.RunAsync(
            Path.GetFullPath(opts.Get("solution-root") ?? "."),
            opts.Required("asr-config"),
            opts.Get("tse"),
            opts.Int("repetitions", 3),
            warmUp: opts.Get("no-warmup") is null,
            parallel: opts.Get("sequential") is null,
            realTime: opts.Get("fast") is null,
            threads: opts.Int("threads", 0),
            streamingThreads: opts.Int("streaming-threads", 1),
            spinning: opts.Get("spinning") is not null,
            opts.Get("label") ?? string.Empty,
            opts.Get("out"), opts.Get("md"));

    case "tse-bench":
        return BonikPOS.VoiceSearch.Evaluation.Tse.TseBenchmark.Run(
            opts.Required("models").Split(',', StringSplitOptions.RemoveEmptyEntries),
            opts.Int("seconds", 10),
            [.. (opts.Get("threads") ?? "1,2").Split(',').Select(t => int.Parse(t, CultureInfo.InvariantCulture))],
            opts.Get("out"), opts.Get("md"));

    case "asr-bench":
        return await BonikPOS.VoiceSearch.Evaluation.Asr.AsrBenchmark.RunAsync(
            opts.Required("configs").Split(',', StringSplitOptions.RemoveEmptyEntries),
            [.. (opts.Get("threads") ?? "1,2,4").Split(',').Select(t => int.Parse(t, CultureInfo.InvariantCulture))],
            opts.Int("repetitions", 5), opts.Int("tokens", 12), opts.Double("seconds", 3),
            opts.Get("out"), opts.Get("md"));

    case "asr-bench-one": // child process of asr-bench
        return BonikPOS.VoiceSearch.Evaluation.Asr.AsrBenchmark.RunOne(
            opts.Required("config"), opts.Int("threads", 1), opts.Int("repetitions", 5), opts.Int("tokens", 12), opts.Double("seconds", 3),
            opts.Required("row-out"));

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'. Use --help.");
        return 2;
}

static async Task<int> RunSearchAsync(Options opts)
{
    string casesPath = opts.Required("cases");
    string catalogArg = opts.Get("catalog") ?? "demo";
    var cases = SearchEvalCase.ReadJsonLines(casesPath);
    IMedicineCatalogSource source = catalogArg == "demo" ? new DemoCatalogSource() : JsonCatalogSource.FromFile(catalogArg);
    var started = DateTimeOffset.UtcNow;
    var (service, indexMs) = await SearchEvaluator.LoadAsync(source, s => new MedicineSearchService(() => s), CancellationToken.None);
    var t = service.Thresholds;
    service.Thresholds = t with
    {
        MinCandidateScore = opts.Double("min-candidate", t.MinCandidateScore),
        ConfidentScore = opts.Double("confident", t.ConfidentScore),
        AmbiguityMargin = opts.Double("margin", t.AmbiguityMargin),
        MinConfirmScore = opts.Double("min-confirm", t.MinConfirmScore),
        MinFuzzyConfirmNameScore = opts.Double("min-fuzzy-confirm", t.MinFuzzyConfirmNameScore),
    };
    var outcomes = await SearchEvaluator.RunAsync(service, cases, CancellationToken.None);
    var metrics = SearchEvaluator.Aggregate(outcomes);
    var byKind = SearchReport.ByKind(outcomes);
    var run = new RunInfo(
        opts.Get("label") ?? Path.GetFileNameWithoutExtension(casesPath),
        casesPath, SearchReport.Sha256(casesPath),
        service.CatalogInfo.SourceDescription, service.CatalogInfo.ProductCount,
        catalogArg == "demo" ? null : SearchReport.Sha256(catalogArg), service.CatalogInfo.IsDemoData, indexMs, started, service.Thresholds);
    string md = SearchReport.Markdown(run, metrics, byKind, outcomes);
    Console.WriteLine(md);
    if (opts.Get("out") is { } jsonPath)
    {
        SearchReport.WriteJson(jsonPath, run, metrics, byKind, outcomes, opts.Get("all-cases") is not null);
    }

    if (opts.Get("md") is { } mdPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mdPath))!);
        await File.WriteAllTextAsync(mdPath, md);
    }

    return 0;
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                key = a[2..];
                o._values[key] = "true";
            }
            else if (key is not null)
            {
                o._values[key] = a;
                key = null;
            }
        }

        return o;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Required(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required");

    public int Int(string name, int fallback) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public double Double(string name, double fallback) => Get(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
}
