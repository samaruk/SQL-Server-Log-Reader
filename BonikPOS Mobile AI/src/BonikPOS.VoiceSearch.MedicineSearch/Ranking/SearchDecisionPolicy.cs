using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.MedicineSearch.Text;

namespace BonikPOS.VoiceSearch.MedicineSearch.Ranking;

/// <summary>
/// Turns ranked candidates into a decision. Principles: never pick between close candidates on the owner's behalf;
/// never present a different strength or form as the requested product; downgrade when speech recognition is unsure
/// or the speaker could not be confirmed as the owner. Even <see cref="SearchDecision.Confident"/> requires the owner
/// to confirm the product in the UI.
/// </summary>
public static class SearchDecisionPolicy
{
    public static (SearchDecision Decision, IReadOnlyList<ClarificationOption> Options, string Explanation) Decide(
        MedicineQuery query, ParsedMedicineQuery parsed, IReadOnlyList<MedicineCandidate> ranked, SearchThresholds thresholds)
    {
        if (ranked.Count == 0)
        {
            return (SearchDecision.NoMatch, [], $"No product in the catalog matches \"{parsed.NameText}\".");
        }

        var top = ranked[0];
        var close = ranked.Where(c => c.Score >= top.Score - thresholds.AmbiguityMargin).ToList();
        // An approximate name match below the configured similarity is shown only as a weak suggestion.
        bool weakFuzzy = top.NameMatch == MatchKind.Fuzzy && thresholds.MinFuzzyConfirmNameScore > 0 && top.NameScore < thresholds.MinFuzzyConfirmNameScore;
        string topBrand = TextNormalizer.Compact(TextNormalizer.Normalize(top.Product.BrandName));
        // The spoken name exists exactly in the catalog, but the best result is a different, similar-sounding name
        // (typically because the exact brand lacks the requested strength or form): never offer the other name.
        // Exact spelling outranks a sound-alike: if the spoken name is a catalog name (by spelling, or by sound when the top
        // result is only similar), a different name must not be offered in its place.
        var exactName = ranked
            .Where(c => c.NameMatch >= MatchKind.ExactPhonetic && IsNameField(c) && BrandKey(c) != topBrand)
            .OrderByDescending(c => c.NameMatch)
            .FirstOrDefault();
        bool otherNameOverExact = exactName is not null && top.NameMatch < exactName.NameMatch;
        // The spoken name has more words than the top match explains and those words form another catalog name exactly
        // ("Napa Extend 500" when only "Napa 500" and "Napa Extend 665" exist): the shorter name is a different product.
        var fullerName = ranked.FirstOrDefault(c => c.NameMatch >= MatchKind.ExactPhonetic && IsNameField(c) &&
                                                    c.NameCoverage > top.NameCoverage + 1e-9 && BrandKey(c) != topBrand);
        bool partialOverFuller = top.NameCoverage < 0.999 && fullerName is not null;

        SearchDecision decision;
        string explanation;
        if (otherNameOverExact || partialOverFuller)
        {
            var asked = (otherNameOverExact ? exactName : fullerName)!;
            decision = SearchDecision.LowConfidence;
            explanation = $"{asked.Product.BrandName} is in the catalog but not as requested; other products are shown only for you to check.";
        }
        else if (top.StrengthMatch == AttributeMatch.Mismatch || top.FormMatch == AttributeMatch.Mismatch)
        {
            // Checked first: a different strength/form must never be offered as the product that was asked for.
            decision = SearchDecision.LowConfidence;
            explanation = top.StrengthMatch == AttributeMatch.Mismatch
                ? $"No {top.Product.BrandName} with the requested strength was found. Other strengths are shown for you to check."
                : $"No {top.Product.BrandName} in the requested form was found. Other forms are shown for you to check.";
        }
        else if (close.Count > 1 && top.Score >= thresholds.MinConfirmScore && !weakFuzzy)
        {
            bool sameBrand = close.All(c => TextNormalizer.Compact(TextNormalizer.Normalize(c.Product.BrandName)) == topBrand);
            decision = SearchDecision.Ambiguous;
            explanation = sameBrand
                ? $"{top.Product.BrandName} is available in {close.Count} strengths or forms. Please choose one."
                : top.MatchedField == "generic"
                    ? $"{close.Count} products contain {top.Product.GenericName}. Please choose the brand."
                    : $"{close.Count} products sound similar. Please choose the right one.";
        }
        else if (top.Score >= thresholds.ConfidentScore && top.NameMatch >= MatchKind.ExactPhonetic && top.MatchedReading is null
                 && top.FormMatch != AttributeMatch.Compatible)
        {
            decision = SearchDecision.Confident;
            explanation = $"Matched {Label(top.Product)}.";
        }
        else if (top.Score >= thresholds.MinConfirmScore && !weakFuzzy)
        {
            decision = SearchDecision.NeedsConfirmation;
            explanation = top.MatchedReading is not null
                ? $"Closest match for another reading of your words (\"{top.MatchedReading}\"): {Label(top.Product)}. Please confirm."
                : top.NameMatch == MatchKind.Fuzzy
                    ? $"Similar name: {Label(top.Product)}. Please confirm it is the medicine you meant."
                    : $"Closest match: {Label(top.Product)}. Please confirm.";
        }
        else
        {
            decision = SearchDecision.LowConfidence;
            explanation = "The match is weak. Please check the suggestions or correct the text.";
        }

        // Upstream uncertainty caps the decision.
        if (query.RecognitionConfidence is double rc && rc < thresholds.MinRecognitionConfidence && decision is SearchDecision.Confident or SearchDecision.NeedsConfirmation)
        {
            decision = SearchDecision.LowConfidence;
            explanation = "Speech recognition was not confident. " + explanation;
        }

        if (query.SpeakerDecision is OwnerVoiceDecision.Uncertain or OwnerVoiceDecision.NoProfile or OwnerVoiceDecision.ModelUnavailable
            && decision == SearchDecision.Confident)
        {
            decision = SearchDecision.NeedsConfirmation;
            explanation = "The owner's voice could not be confirmed. " + explanation;
        }

        var options = decision == SearchDecision.Ambiguous
            ? close.Select(c => new ClarificationOption(Label(c.Product), c.Product.ProductId)).ToList()
            : (IReadOnlyList<ClarificationOption>)[];
        return (decision, options, explanation);
    }

    private static bool IsNameField(MedicineCandidate c) => c.MatchedField is "brand" or "local name" or "alias";

    private static string BrandKey(MedicineCandidate c) => TextNormalizer.Compact(TextNormalizer.Normalize(c.Product.BrandName));

    public static string Label(MedicineProduct p)
    {
        var parts = new List<string> { p.BrandName };
        if (!string.IsNullOrWhiteSpace(p.Strength))
        {
            parts.Add(p.Strength);
        }

        if (!string.IsNullOrWhiteSpace(p.DosageForm))
        {
            parts.Add(p.DosageForm);
        }

        return string.Join(' ', parts);
    }
}
