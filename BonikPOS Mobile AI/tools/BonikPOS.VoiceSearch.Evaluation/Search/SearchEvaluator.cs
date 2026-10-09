using System.Diagnostics;
using BonikPOS.VoiceSearch.Core.Medicine;

namespace BonikPOS.VoiceSearch.Evaluation.Search;

/// <summary>Outcome of one case.</summary>
public sealed record SearchCaseOutcome(
    SearchEvalCase Case,
    SearchDecision Decision,
    IReadOnlyList<string> RankedIds,
    IReadOnlyList<string> RankedLabels,
    int? CorrectRank,
    bool FalseMatch,
    bool FalseConfident,
    bool WrongOffered,
    bool? DecisionAsExpected,
    double MatcherMs,
    int SuggestionCount);

public sealed record RateValue(int Count, int Total)
{
    /// <summary>Null when there were no cases to rate.</summary>
    public double? Rate => Total == 0 ? null : (double)Count / Total;

    public override string ToString() => Total == 0 ? "n/a" : $"{Rate:P1} ({Count}/{Total})";
}

/// <summary>Aggregated metrics. Definitions are in docs/SEARCH_EVALUATION.md and repeated in the report.</summary>
public sealed record SearchMetrics(
    int Cases,
    int Positives,
    int Negatives,
    RateValue Top1,
    RateValue Top5,
    RateValue FalseMatchAll,
    RateValue FalseMatchPositives,
    RateValue FalseMatchNegatives,
    RateValue FalseConfident,
    RateValue WrongOfferedAll,
    RateValue WrongOfferedNegatives,
    RateValue CorrectButLowConfidence,
    RateValue MissedPositives,
    RateValue NegativesRejected,
    RateValue NegativesSafe,
    RateValue DecisionAgreement,
    IReadOnlyDictionary<string, int> DecisionCounts,
    double MatcherP50Ms,
    double MatcherP95Ms,
    double MatcherMaxMs);

public static class SearchEvaluator
{
    private static readonly SearchDecision[] Asserting = [SearchDecision.Confident, SearchDecision.NeedsConfirmation];

    public static async Task<IReadOnlyList<SearchCaseOutcome>> RunAsync(IMedicineSearchService search, IReadOnlyList<SearchEvalCase> cases, CancellationToken ct)
    {
        var outcomes = new List<SearchCaseOutcome>(cases.Count);
        foreach (var c in cases)
        {
            var result = await search.SearchAsync(c.ToQuery(), ct).ConfigureAwait(false);
            outcomes.Add(Score(c, result));
        }

        return outcomes;
    }

    public static SearchCaseOutcome Score(SearchEvalCase c, MedicineSearchResult result)
    {
        var ids = result.Candidates.Select(x => x.Product.ProductId).ToList();
        var labels = result.Candidates.Select(x => $"{x.Product.ProductId} {x.Product.BrandName} {x.Product.Strength} {x.Product.DosageForm} [{x.Score:0.00}]").ToList();
        var accept = new HashSet<string>(c.Expect.Products, StringComparer.Ordinal);
        int? rank = null;
        for (int i = 0; i < ids.Count; i++)
        {
            if (accept.Contains(ids[i]))
            {
                rank = i + 1;
                break;
            }
        }

        bool topWrong = ids.Count > 0 && !accept.Contains(ids[0]);
        bool asserting = Asserting.Contains(result.Decision);
        bool falseMatch = asserting && topWrong;
        bool falseConfident = result.Decision == SearchDecision.Confident && topWrong;
        bool? decisionOk = c.Expect.Decisions.Count == 0 ? null : c.Expect.Decisions.Contains(result.Decision.ToString());
        // Wrong product offered: a false match, or an "Ambiguous" choice in which none of the options is correct.
        bool wrongOffered = falseMatch ||
            (result.Decision == SearchDecision.Ambiguous && !result.Clarifications.Any(o => accept.Contains(o.ProductId)));
        return new SearchCaseOutcome(c, result.Decision, ids, labels, rank, falseMatch, falseConfident, wrongOffered, decisionOk,
            result.Elapsed.TotalMilliseconds, result.Suggestions.Count);
    }

    public static SearchMetrics Aggregate(IReadOnlyList<SearchCaseOutcome> outcomes)
    {
        var pos = outcomes.Where(o => !o.Case.IsNegative).ToList();
        var neg = outcomes.Where(o => o.Case.IsNegative).ToList();
        var withDecision = outcomes.Where(o => o.DecisionAsExpected.HasValue).ToList();
        var times = outcomes.Select(o => o.MatcherMs).Order().ToArray();
        return new SearchMetrics(
            outcomes.Count, pos.Count, neg.Count,
            new RateValue(pos.Count(o => o.CorrectRank == 1), pos.Count),
            new RateValue(pos.Count(o => o.CorrectRank is <= 5), pos.Count),
            new RateValue(outcomes.Count(o => o.FalseMatch), outcomes.Count),
            new RateValue(pos.Count(o => o.FalseMatch), pos.Count),
            new RateValue(neg.Count(o => o.FalseMatch), neg.Count),
            new RateValue(outcomes.Count(o => o.FalseConfident), outcomes.Count),
            new RateValue(outcomes.Count(o => o.WrongOffered), outcomes.Count),
            new RateValue(neg.Count(o => o.WrongOffered), neg.Count),
            new RateValue(pos.Count(o => o.CorrectRank == 1 && o.Decision is SearchDecision.LowConfidence or SearchDecision.NoMatch), pos.Count),
            new RateValue(pos.Count(o => o.Decision == SearchDecision.NoMatch), pos.Count),
            new RateValue(neg.Count(o => o.Decision == SearchDecision.NoMatch), neg.Count),
            new RateValue(neg.Count(o => o.Decision is SearchDecision.NoMatch or SearchDecision.LowConfidence), neg.Count),
            new RateValue(withDecision.Count(o => o.DecisionAsExpected == true), withDecision.Count),
            Enum.GetValues<SearchDecision>().ToDictionary(d => d.ToString(), d => outcomes.Count(o => o.Decision == d)),
            Percentile(times, 0.50), Percentile(times, 0.95), times.Length == 0 ? 0 : times[^1]);
    }

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        double pos = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    public static async Task<(IMedicineSearchService Service, double IndexMs)> LoadAsync(IMedicineCatalogSource source, Func<IMedicineCatalogSource, IMedicineSearchService> factory, CancellationToken ct)
    {
        var service = factory(source);
        var sw = Stopwatch.StartNew();
        await service.InitializeAsync(ct).ConfigureAwait(false);
        return (service, sw.Elapsed.TotalMilliseconds);
    }
}
