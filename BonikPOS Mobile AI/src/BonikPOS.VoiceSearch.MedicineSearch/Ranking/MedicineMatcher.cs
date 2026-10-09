using System.Diagnostics;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.MedicineSearch.Index;
using BonikPOS.VoiceSearch.MedicineSearch.Text;

namespace BonikPOS.VoiceSearch.MedicineSearch.Ranking;

/// <summary>
/// Exact matching first (normalised text, then phonetic key), then retrieval by consonant skeleton and trigrams, and
/// weighted phonetic ranking. Strength and dosage-form constraints adjust scores but never turn a different product
/// into a match: a requested strength that does not exist is reported as a mismatch. Alternative recogniser readings
/// are searched as correction candidates; a product found only through an alternative is never "Confident".
/// </summary>
public sealed class MedicineMatcher
{
    public const int MaxNameTokens = 12;
    public const int MaxWindowTokens = 4;
    public const int TrigramCandidates = 250;
    public const int MaxAlternatives = 4;
    public const int MaxSuggestions = 3;

    /// <summary>Score factor for a product found only through an alternative reading (ties go to the primary text).</summary>
    public const double AlternativeFactor = 0.97;

    /// <summary>Name factor for a generic-name match: a generic fits every brand of that molecule.</summary>
    public const double GenericFactor = 0.92;

    /// <summary>Additional factor when only one molecule of a combination product matched.</summary>
    public const double ComponentFactor = 0.85;

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
        var scored = new Dictionary<int, MedicineCandidate>();
        bool hasName = HasName(parsed);
        if (hasName)
        {
            ScoreReading(parsed, null, scored);
        }

        foreach (string alternative in Alternatives(query))
        {
            var altParsed = QueryParser.Parse(alternative);
            if (HasName(altParsed))
            {
                hasName = true;
                ScoreReading(altParsed, alternative, scored);
            }
        }

        if (!hasName)
        {
            return new MedicineSearchResult(query, parsed, SearchDecision.NoMatch, [], [],
                "No medicine name was recognised in the query.", sw.Elapsed, catalog);
        }

        var learned = _hints?.GetLearnedProducts(TextNormalizer.Compact(parsed.NormalizedText)) ?? [];
        foreach (string id in learned)
        {
            if (_index.Find(id) is { } p)
            {
                var c = Score(p, null, 1.0, MatchKind.Exact, parsed, []);
                scored[p.Index] = c with { Notes = [.. c.Notes, "You chose this product for the same words before."] };
            }
        }

        var ranked = scored.Values
            .Where(c => c.Score >= thresholds.MinCandidateScore)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.MatchedReading is null ? 0 : 1)
            .ThenByDescending(c => _hints?.GetPopularity(c.Product.ProductId) ?? 0)
            .ThenBy(c => c.Product.BrandName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Product.ProductId, StringComparer.Ordinal)
            .Take(thresholds.MaxCandidates)
            .ToList();

        var (decision, clarifications, explanation) = SearchDecisionPolicy.Decide(query, parsed, ranked, thresholds);
        var suggestions = decision == SearchDecision.NoMatch ? Suggest(scored.Values, parsed, thresholds) : [];
        return new MedicineSearchResult(query, parsed, decision, ranked, clarifications, explanation, sw.Elapsed, catalog)
        {
            Suggestions = suggestions,
        };
    }

    private static bool HasName(ParsedMedicineQuery p) => p.NameText.Length > 0 || p.NameWithFormsText.Length > 0;

    private static IEnumerable<string> Alternatives(MedicineQuery query) =>
        query.Alternatives
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Where(a => !string.Equals(a, query.Text.Trim(), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxAlternatives);

    /// <summary>Scores every retrieved name variant for one reading and keeps the best candidate per product.</summary>
    private void ScoreReading(ParsedMedicineQuery parsed, string? alternative, Dictionary<int, MedicineCandidate> scored)
    {
        var windows = BuildWindows(parsed.NameText, consumesForms: false);
        if (parsed.BareNumbers.Count > 0 && parsed.NameWithNumbersText != parsed.NameText)
        {
            // Brands that contain digits ("Cef-3"): also try the name with the numbers in place.
            windows.AddRange(BuildWindows(parsed.NameWithNumbersText, consumesForms: false).Where(w => w.Compact.Any(char.IsDigit)));
        }

        if (parsed.NameWithFormsText.Length > 0)
        {
            // Names a recogniser split into a form word ("sir gel" -> Sergel): windows that include the form word.
            windows.AddRange(BuildWindows(parsed.NameWithFormsText, consumesForms: true).Where(w => w.ConsumedForms.Count > 0));
        }

        foreach (int v in CandidateVariants(windows, parsed.NamePhoneticKey))
        {
            var variant = _index.Variants[v];
            var (nameScore, kind, window) = ScoreName(variant, windows);
            nameScore *= variant.Kind switch
            {
                NameVariantKind.Generic => GenericFactor,
                NameVariantKind.GenericComponent => GenericFactor * ComponentFactor,
                _ => 1.0,
            };

            var candidate = Score(_index.Products[variant.ProductIndex], variant, nameScore, kind, parsed, window?.ConsumedForms ?? []) with
            {
                NameCoverage = window is null ? 1.0 : (double)window.Tokens / window.TotalTokens,
            };
            if (alternative is not null)
            {
                candidate = candidate with
                {
                    Score = candidate.Score * AlternativeFactor,
                    MatchedReading = alternative,
                    Notes = [.. candidate.Notes, $"Matched another reading of your words: \"{alternative}\"."],
                };
            }

            if (!scored.TryGetValue(variant.ProductIndex, out var existing) || candidate.Score > existing.Score)
            {
                scored[variant.ProductIndex] = candidate;
            }
        }
    }

    private static List<QueryWindow> BuildWindows(string nameText, bool consumesForms)
    {
        var tokens = TextNormalizer.Tokenize(nameText).Take(MaxNameTokens).ToArray();
        var forms = consumesForms
            ? tokens.Select(t => MedicineVocabulary.TryForm(t, out var f) ? f : (DosageFormCategory?)null).ToArray()
            : new DosageFormCategory?[tokens.Length];
        var windows = new List<QueryWindow>();
        for (int start = 0; start < tokens.Length; start++)
        {
            for (int len = 1; len <= MaxWindowTokens && start + len <= tokens.Length; len++)
            {
                string text = string.Join(' ', tokens, start, len);
                var consumed = forms.Skip(start).Take(len).Where(f => f is not null).Select(f => f!.Value).Distinct().ToArray();
                if (consumed.Length == len)
                {
                    continue; // a window made only of form words is not a name
                }

                bool conversational = tokens.Skip(start).Take(len).All(MedicineVocabulary.IsConversational);
                windows.Add(new QueryWindow(TextNormalizer.Compact(text), PhoneticKey.For(text), len, tokens.Length, consumed, conversational));
            }
        }

        return windows;
    }

    private HashSet<int> CandidateVariants(List<QueryWindow> windows, string fullKey)
    {
        var result = new HashSet<int>();
        foreach (var w in windows)
        {
            result.UnionWith(_index.ExactByCompact(w.Compact));
            result.UnionWith(_index.ExactByKey(w.Key));
            result.UnionWith(_index.ExactBySkeleton(w.Key));
        }

        var touched = new List<int>();
        lock (_countsGate)
        {
            try
            {
                _index.CountTrigramHits(fullKey, _counts, touched);
                foreach (var w in windows.Where(w => w.Tokens == 1 || w.ConsumedForms.Count > 0))
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

    private static (double Score, MatchKind Kind, QueryWindow? Window) ScoreName(NameVariant variant, List<QueryWindow> windows)
    {
        double best = 0;
        var bestKind = MatchKind.None;
        QueryWindow? bestWindow = null;
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
            else if (w.Conversational)
            {
                continue; // "thank you", "ki khobor": never approximately a brand
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
                bestWindow = w;
            }
        }

        return (best, bestKind, bestWindow);
    }

    private static MedicineCandidate Score(
        IndexedProduct p, NameVariant? variant, double nameScore, MatchKind kind, ParsedMedicineQuery q, IReadOnlyCollection<DosageFormCategory> consumedForms)
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
        var requestedForms = consumedForms.Count == 0 ? q.Forms : q.Forms.Where(f => !consumedForms.Contains(f)).ToList();
        if (requestedForms.Count > 0)
        {
            if (p.Form is DosageFormCategory.Unknown or DosageFormCategory.Other)
            {
                form = AttributeMatch.Unknown;
                factor *= 0.95;
            }
            else if (requestedForms.Contains(p.Form) || requestedForms.Any(f => p.FormMentions.Contains(f)))
            {
                form = AttributeMatch.Match;
            }
            else if (requestedForms.Any(f => MedicineVocabulary.AreCompatible(f, p.Form)))
            {
                form = AttributeMatch.Compatible;
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

        if (variant?.Kind is NameVariantKind.Generic)
        {
            notes.Add($"Matched the generic name {p.Product.GenericName}.");
        }
        else if (variant?.Kind is NameVariantKind.GenericComponent)
        {
            notes.Add($"Contains {variant.Text}; the product is {p.Product.GenericName}.");
        }

        string field = variant?.Kind switch
        {
            NameVariantKind.Generic or NameVariantKind.GenericComponent => "generic",
            NameVariantKind.LocalName => "local name",
            NameVariantKind.Alias => "alias",
            null => "learned",
            _ => "brand",
        };
        return new MedicineCandidate(p.Product, nameScore * factor, nameScore, kind, field, strength, form, notes);
    }

    /// <summary>Near-miss names for the no-match screen: brand spellings from the catalog, never applied automatically.</summary>
    private static List<CorrectionSuggestion> Suggest(IEnumerable<MedicineCandidate> all, ParsedMedicineQuery parsed, SearchThresholds thresholds)
    {
        var suffix = new List<string>();
        if (parsed.Strengths.Count > 0)
        {
            suffix.AddRange(parsed.Strengths.Select(s => s.ToString()));
        }
        else
        {
            suffix.AddRange(parsed.BareNumbers.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        suffix.AddRange(parsed.Forms.Select(f => f.ToString().ToLowerInvariant()));
        string tail = suffix.Count == 0 ? string.Empty : " " + string.Join(' ', suffix);
        return all
            .Where(c => c.MatchedField == "brand" && c.NameScore >= thresholds.SuggestionMinNameScore)
            .OrderByDescending(c => c.NameScore)
            .ThenBy(c => c.Product.BrandName, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(c => TextNormalizer.Compact(TextNormalizer.Normalize(c.Product.BrandName)))
            .Take(MaxSuggestions)
            .Select(c => new CorrectionSuggestion(c.Product.BrandName + tail, c.Product.BrandName, Math.Round(c.NameScore, 3)))
            .ToList();
    }

    private static bool BrandContainsNumber(IndexedProduct p, decimal n) =>
        TextNormalizer.Tokenize(TextNormalizer.Normalize(p.Product.BrandName))
            .Contains(n.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <param name="Conversational">All tokens are everyday conversational words: only exact matches are allowed.</param>
    private sealed record QueryWindow(
        string Compact, string Key, int Tokens, int TotalTokens, IReadOnlyCollection<DosageFormCategory> ConsumedForms, bool Conversational);
}
