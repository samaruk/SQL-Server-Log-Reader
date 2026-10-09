using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Medicine;

namespace BonikPOS.VoiceSearch.Evaluation.Search;

public sealed record RunInfo(
    string Label,
    string DatasetPath,
    string DatasetSha256,
    string CatalogDescription,
    int CatalogProducts,
    string? CatalogSha256,
    bool CatalogIsDemo,
    double IndexBuildMs,
    DateTimeOffset StartedUtc,
    SearchThresholds Thresholds)
{
    public string Machine { get; init; } =
        $"{RuntimeInformation.OSDescription}; {RuntimeInformation.OSArchitecture}; {Environment.ProcessorCount} logical CPUs; .NET {Environment.Version}";
}

/// <summary>Writes the JSON and Markdown evaluation reports. Every number comes from the outcomes passed in.</summary>
public static class SearchReport
{
    public static readonly string Definitions = string.Join('\n',
        "- Positive case: at least one catalog product satisfies the request; any listed product id counts as correct.",
        "- Negative case: nothing in the catalog satisfies the request (unknown brand, non-existent strength/form, not a medicine).",
        "- Top-1 / Top-5 accuracy: share of positive cases whose first / any of the first five ranked products is correct.",
        "- False match: the system presented a wrong product as the match (decision Confident or NeedsConfirmation with a wrong first result); counted on all cases, so every Confident/NeedsConfirmation answer to a negative case is a false match.",
        "- False confident: the subset of false matches with decision Confident.",
        "- Wrong product offered: a false match, or an Ambiguous choice in which none of the offered options is correct.",
        "- Correct but low confidence: positive cases whose first result is correct but which were answered LowConfidence/NoMatch (shown only as a weak suggestion).",
        "- Negatives safely handled: decision NoMatch or LowConfidence (candidates shown only with a warning).",
        "- Missed positives: decision NoMatch although a correct product exists.",
        "- Decision agreement: share of cases with an expected decision whose decision was one of the expected ones.",
        "- Matcher latency: time inside the matcher per query on the machine named in the report (not a phone).");

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <param name="allCases">False (default): only failing cases are listed, which keeps reports small enough to commit.</param>
    public static void WriteJson(string path, RunInfo run, SearchMetrics metrics, IReadOnlyDictionary<string, SearchMetrics> byKind, IReadOnlyList<SearchCaseOutcome> outcomes, bool allCases = false)
    {
        var listed = allCases ? outcomes : outcomes.Where(IsFailure).ToList();
        var doc = new Dictionary<string, object?>
        {
            ["format"] = "bonikpos-search-eval-report/1",
            ["run"] = run,
            ["metrics"] = metrics,
            ["by_kind"] = byKind,
            ["definitions"] = Definitions.Split('\n'),
            ["cases_listed"] = allCases ? "all" : "failures only (wrong or missing top-1, false match, wrong product offered, unexpected decision)",
            ["cases"] = listed.Select(o => new
            {
                id = o.Case.Id,
                query = o.Case.Query,
                kind = o.Case.Kind,
                expected = o.Case.Expect.Products,
                expected_decisions = o.Case.Expect.Decisions,
                decision = o.Decision.ToString(),
                correct_rank = o.CorrectRank,
                false_match = o.FalseMatch,
                false_confident = o.FalseConfident,
                wrong_offered = o.WrongOffered,
                decision_as_expected = o.DecisionAsExpected,
                top = o.RankedLabels.Take(5),
                suggestions = o.SuggestionCount,
                matcher_ms = Math.Round(o.MatcherMs, 3),
            }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    public static string Markdown(RunInfo run, SearchMetrics m, IReadOnlyDictionary<string, SearchMetrics> byKind, IReadOnlyList<SearchCaseOutcome> outcomes, int maxFailures = 60)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"### {run.Label}");
        sb.AppendLine();
        sb.AppendLine(inv, $"- Dataset: `{Path.GetFileName(run.DatasetPath)}` (sha256 `{run.DatasetSha256[..16]}…`), {m.Cases} cases: {m.Positives} positive, {m.Negatives} negative");
        sb.AppendLine(inv, $"- Catalog: {run.CatalogDescription}; {run.CatalogProducts} products{(run.CatalogSha256 is null ? string.Empty : $" (sha256 `{run.CatalogSha256[..16]}…`)")}; index build {run.IndexBuildMs:0} ms");
        sb.AppendLine(inv, $"- Run: {run.StartedUtc:yyyy-MM-dd HH:mm} UTC on {run.Machine}");
        var t = run.Thresholds;
        sb.AppendLine(inv, $"- Thresholds: min candidate {t.MinCandidateScore:0.00}, confident {t.ConfidentScore:0.00}, ambiguity margin {t.AmbiguityMargin:0.00}, min confirm {t.MinConfirmScore:0.00}, min fuzzy confirm {t.MinFuzzyConfirmNameScore:0.00}, min recognition confidence {t.MinRecognitionConfidence:0.00}");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(inv, $"| Top-1 accuracy (positives) | {m.Top1} |");
        sb.AppendLine(inv, $"| Top-5 accuracy (positives) | {m.Top5} |");
        sb.AppendLine(inv, $"| False matches (all cases) | {m.FalseMatchAll} |");
        sb.AppendLine(inv, $"| — on positive cases | {m.FalseMatchPositives} |");
        sb.AppendLine(inv, $"| — on negative cases | {m.FalseMatchNegatives} |");
        sb.AppendLine(inv, $"| False matches with decision Confident | {m.FalseConfident} |");
        sb.AppendLine(inv, $"| Wrong product offered (false match or all-wrong Ambiguous), all cases | {m.WrongOfferedAll} |");
        sb.AppendLine(inv, $"| — on negative cases | {m.WrongOfferedNegatives} |");
        sb.AppendLine(inv, $"| Correct top-1 but answered LowConfidence/NoMatch (positives) | {m.CorrectButLowConfidence} |");
        sb.AppendLine(inv, $"| Negatives safely handled (NoMatch/LowConfidence) | {m.NegativesSafe} |");
        sb.AppendLine(inv, $"| Negatives answered NoMatch | {m.NegativesRejected} |");
        sb.AppendLine(inv, $"| Positives answered NoMatch (missed) | {m.MissedPositives} |");
        sb.AppendLine(inv, $"| Decision agreement (cases with expected decision) | {m.DecisionAgreement} |");
        sb.AppendLine(inv, $"| Decisions | {string.Join(", ", m.DecisionCounts.Where(p => p.Value > 0).Select(p => $"{p.Key} {p.Value}"))} |");
        sb.AppendLine(inv, $"| Matcher latency p50 / p95 / max | {m.MatcherP50Ms:0.00} / {m.MatcherP95Ms:0.00} / {m.MatcherMaxMs:0.00} ms |");
        sb.AppendLine();
        sb.AppendLine("| Category | Cases | Top-1 | Top-5 | False matches | Wrong offered | Negatives safe |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var (kind, km) in byKind.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(inv, $"| {kind} | {km.Cases} | {Short(km.Top1)} | {Short(km.Top5)} | {km.FalseMatchAll.Count} | {km.WrongOfferedAll.Count} | {Short(km.NegativesSafe)} |");
        }

        var failures = outcomes.Where(IsFailure).ToList();
        sb.AppendLine();
        sb.AppendLine(inv, $"Failures ({failures.Count}; wrong or missing top-1, false matches, unexpected decisions){(failures.Count > maxFailures ? $" — first {maxFailures}" : string.Empty)}:");
        sb.AppendLine();
        foreach (var o in failures.Take(maxFailures))
        {
            string what = o.FalseMatch ? "FALSE MATCH" : o.WrongOffered ? "wrong products offered as a choice" : o.Case.IsNegative ? "negative" : o.CorrectRank is null ? "correct product not ranked" : $"correct at rank {o.CorrectRank}";
            if (o.DecisionAsExpected == false)
            {
                what += $"; expected decision {string.Join("/", o.Case.Expect.Decisions)}";
            }

            sb.AppendLine(inv, $"- `{o.Case.Id}` \"{o.Case.Query}\" → {o.Decision}; {what}; top: {(o.RankedLabels.Count == 0 ? "(none)" : o.RankedLabels[0])}");
        }

        return sb.ToString();

        static string Short(RateValue r) => r.Total == 0 ? "–" : $"{r.Rate:P0} ({r.Count}/{r.Total})";
    }

    public static bool IsFailure(SearchCaseOutcome o) =>
        o.FalseMatch || o.WrongOffered || (!o.Case.IsNegative && o.CorrectRank != 1) || o.DecisionAsExpected == false;

    public static IReadOnlyDictionary<string, SearchMetrics> ByKind(IReadOnlyList<SearchCaseOutcome> outcomes) =>
        outcomes.SelectMany(o => o.Case.Kind.Select(k => (k, o)))
            .GroupBy(p => p.k, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => SearchEvaluator.Aggregate([.. g.Select(p => p.o)]), StringComparer.Ordinal);
}
