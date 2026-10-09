using System.Diagnostics;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.MedicineSearch.Index;
using BonikPOS.VoiceSearch.MedicineSearch.Text;

namespace BonikPOS.VoiceSearch.MedicineSearch.Ranking;

/// <summary>
/// Exact matching first (normalised text, then phonetic key), then trigram retrieval and weighted phonetic ranking.
/// Strength and dosage-form constraints adjust scores but never turn a different product into a match: a requested
/// strength that does not exist is reported as a mismatch.
/// </summary>
public sealed class MedicineMatcher
{
    public const int MaxNameTokens = 12;
    public const int MaxWindowTokens = 4;
    public const int TrigramCandidates = 250;

    private readonly MedicineIndex _index;
    private readonly IMedicineSearchHints? _hints;
    private readonly int[] _counts;
    private readonly Lock _countsGate = new();

    public MedicineMatcher(MedicineIndex index, IMedicineSearchHints? hints = null)
    {
        _index = index;
        _hints = hints;
        _counts = new int[index.Variants.Count];
    }

    public MedicineSearchResult Search(MedicineQuery query, SearchThresholds thresholds, MedicineCatalogInfo catalog)
    {
        var sw = Stopwatch.StartNew();
        var parsed = QueryParser.Parse(query.Text);
        if (parsed.NameText.Length == 0)
        {
            return new MedicineSearchResult(query, parsed, SearchDecision.NoMatch, [], [],
                "No medicine name was recognised in the query.", sw.Elapsed, catalog);
        }

        var windows = BuildWindows(parsed.NameText);
        if (parsed.BareNumbers.Count > 0 && parsed.NameWithNumbersText != parsed.NameText)
        {
            // Brands that contain digits ("Cef-3", "Calbo-D 500"?): also try the name with the numbers in place.
            windows.AddRange(BuildWindows(parsed.NameWithNumbersText).Where(w => w.Compact.Any(char.IsDigit)));
        }
        var learned = _hints?.GetLearnedProducts(TextNormalizer.Compact(parsed.NormalizedText)) ?? [];
        var scored = new Dictionary<int, MedicineCandidate>();
        foreach (int v in CandidateVariants(windows, parsed.NamePhoneticKey))
        {
            var variant = _index.Variants[v];
            var (nameScore, kind) = ScoreName(variant, windows);
            if (variant.Kind == NameVariantKind.Generic)
            {
                nameScore *= 0.92; // a generic name fits every brand of that molecule
            }

            if (scored.TryGetValue(variant.ProductIndex, out var existing) && existing.NameScore >= nameScore)
            {
                continue;
            }

            scored[variant.ProductIndex] = Score(_index.Products[variant.ProductIndex], variant, nameScore, kind, parsed);
        }

        foreach (string id in learned)
        {
            if (_index.Find(id) is { } p)
            {
                var c = Score(p, null, 1.0, MatchKind.Exact, parsed);
                scored[p.Index] = c with { Notes = [.. c.Notes, "You chose this product for the same words before."] };
            }
        }

        var ranked = scored.Values
            .Where(c => c.Score >= thresholds.MinCandidateScore)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => _hints?.GetPopularity(c.Product.ProductId) ?? 0)
            .ThenBy(c => c.Product.BrandName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Product.ProductId, StringComparer.Ordinal)
            .Take(thresholds.MaxCandidates)
            .ToList();

        var (decision, clarifications, explanation) = SearchDecisionPolicy.Decide(query, parsed, ranked, thresholds);
        return new MedicineSearchResult(query, parsed, decision, ranked, clarifications, explanation, sw.Elapsed, catalog);
    }

    private List<QueryWindow> BuildWindows(string nameText)
    {
        var tokens = TextNormalizer.Tokenize(nameText).Take(MaxNameTokens).ToArray();
        var windows = new List<QueryWindow>();
        for (int start = 0; start < tokens.Length; start++)
        {
            for (int len = 1; len <= MaxWindowTokens && start + len <= tokens.Length; len++)
            {
                string text = string.Join(' ', tokens, start, len);
                windows.Add(new QueryWindow(TextNormalizer.Compact(text), PhoneticKey.For(text), len, tokens.Length));
            }
        }

        return windows;
    }

    private IEnumerable<int> CandidateVariants(List<QueryWindow> windows, string fullKey)
    {
        var result = new HashSet<int>();
        foreach (var w in windows)
        {
            result.UnionWith(_index.ExactByCompact(w.Compact));
            result.UnionWith(_index.ExactByKey(w.Key));
        }

        var touched = new List<int>();
        lock (_countsGate)
        {
        try
        {
            _index.CountTrigramHits(fullKey, _counts, touched);
            foreach (var w in windows.Where(w => w.Tokens == 1))
            {
                _index.CountTrigramHits(w.Key, _counts, touched);
            }

            foreach (int v in touched.OrderByDescending(v => _counts[v]).Take(TrigramCandidates))
            {
                result.Add(v);
            }
        }
        finally
        {
            foreach (int v in touched)
            {
                _counts[v] = 0;
            }
        }
        }

        return result;
    }

    private static (double Score, MatchKind Kind) ScoreName(NameVariant variant, List<QueryWindow> windows)
    {
        double best = 0;
        var bestKind = MatchKind.None;
        foreach (var w in windows)
        {
            double sim;
            MatchKind kind;
            if (w.Compact == variant.Compact)
            {
                (sim, kind) = (1.0, MatchKind.Exact);
            }
            else if (w.Key.Length > 0 && w.Key == variant.Key)
            {
                (sim, kind) = (0.96, MatchKind.ExactPhonetic);
            }
            else if (w.Compact.Length >= 3 && variant.Compact.StartsWith(w.Compact, StringComparison.Ordinal))
            {
                (sim, kind) = (0.78 + 0.14 * w.Compact.Length / variant.Compact.Length, MatchKind.Prefix);
            }
            else
            {
                double phonetic = PhoneticSimilarity.Similarity(w.Key, variant.Key);
                double spelling = PhoneticSimilarity.SpellingSimilarity(w.Compact, variant.Compact) * 0.98;
                (sim, kind) = (Math.Max(phonetic, spelling), MatchKind.Fuzzy);
            }

            // Prefer readings that explain more of the spoken name ("napa extra" over "napa").
            double coverage = (double)w.Tokens / w.TotalTokens;
            double adjusted = sim * (0.85 + 0.15 * coverage);
            if (adjusted > best || (adjusted == best && kind > bestKind))
            {
                best = adjusted;
                bestKind = kind;
            }
        }

        return (best, bestKind);
    }

    private static MedicineCandidate Score(IndexedProduct p, NameVariant? variant, double nameScore, MatchKind kind, ParsedMedicineQuery q)
    {
        var notes = new List<string>();
        double factor = 1.0;
        AttributeMatch strength = AttributeMatch.NotRequested;
        if (q.Strengths.Count > 0)
        {
            if (p.Strengths.Count == 0)
            {
                strength = AttributeMatch.Unknown;
                factor *= 0.9;
                notes.Add("The catalog has no strength for this product.");
            }
            else if (q.Strengths.All(s => p.Strengths.Contains(s)))
            {
                strength = AttributeMatch.Match;
            }
            else
            {
                strength = AttributeMatch.Mismatch;
                factor *= 0.75;
                notes.Add($"Different strength: you asked for {string.Join(" + ", q.Strengths)}.");
            }
        }
        else if (q.BareNumbers.Any(n => !BrandContainsNumber(p, n)))
        {
            var numbers = q.BareNumbers.Where(n => !BrandContainsNumber(p, n)).ToList();
            if (p.Strengths.Count == 0)
            {
                strength = AttributeMatch.Unknown;
                factor *= 0.95;
            }
            else if (numbers.All(n => p.Strengths.Any(s => s.Amount == n)))
            {
                strength = AttributeMatch.Match;
            }
            else
            {
                strength = AttributeMatch.Mismatch;
                factor *= 0.85;
                notes.Add($"Different strength: you said {string.Join(", ", numbers)}.");
            }
        }

        AttributeMatch form = AttributeMatch.NotRequested;
        if (q.Forms.Count > 0)
        {
            if (p.Form is DosageFormCategory.Unknown or DosageFormCategory.Other)
            {
                form = AttributeMatch.Unknown;
                factor *= 0.95;
            }
            else if (q.Forms.Contains(p.Form))
            {
                form = AttributeMatch.Match;
            }
            else if (q.Forms.Any(f => MedicineVocabulary.AreCompatible(f, p.Form)))
            {
                form = AttributeMatch.Match;
                factor *= 0.97;
                notes.Add($"Listed as {p.Product.DosageForm}.");
            }
            else
            {
                form = AttributeMatch.Mismatch;
                factor *= 0.8;
                notes.Add($"Different form: listed as {p.Product.DosageForm}.");
            }
        }

        if (variant?.Kind == NameVariantKind.Generic)
        {
            notes.Add($"Matched the generic name {p.Product.GenericName}.");
        }

        string field = variant?.Kind switch
        {
            NameVariantKind.Generic => "generic",
            NameVariantKind.LocalName => "local name",
            NameVariantKind.Alias => "alias",
            null => "learned",
            _ => "brand",
        };
        return new MedicineCandidate(p.Product, nameScore * factor, nameScore, kind, field, strength, form, notes);
    }

    private static bool BrandContainsNumber(IndexedProduct p, decimal n) =>
        TextNormalizer.Tokenize(TextNormalizer.Normalize(p.Product.BrandName))
            .Contains(n.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private sealed record QueryWindow(string Compact, string Key, int Tokens, int TotalTokens);
}
