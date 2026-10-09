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
        string topBrand = TextNormalizer.Compact(TextNormalizer.Normalize(top.Product.BrandName));

        SearchDecision decision;
        string explanation;
        if (top.StrengthMatch == AttributeMatch.Mismatch || top.FormMatch == AttributeMatch.Mismatch)
        {
            // Checked first: a different strength/form must never be offered as the product that was asked for.
            decision = SearchDecision.LowConfidence;
            explanation = top.StrengthMatch == AttributeMatch.Mismatch
                ? $"No {top.Product.BrandName} with the requested strength was found. Other strengths are shown for you to check."
                : $"No {top.Product.BrandName} in the requested form was found. Other forms are shown for you to check.";
        }
        else if (close.Count > 1)
        {
            bool sameBrand = close.All(c => TextNormalizer.Compact(TextNormalizer.Normalize(c.Product.BrandName)) == topBrand);
            decision = SearchDecision.Ambiguous;
            explanation = sameBrand
                ? $"{top.Product.BrandName} is available in {close.Count} strengths or forms. Please choose one."
                : top.MatchedField == "generic"
                    ? $"{close.Count} products contain {top.Product.GenericName}. Please choose the brand."
                    : $"{close.Count} products sound similar. Please choose the right one.";
        }
        else if (top.Score >= thresholds.ConfidentScore && top.NameMatch >= MatchKind.ExactPhonetic)
        {
            decision = SearchDecision.Confident;
            explanation = $"Matched {Label(top.Product)}.";
        }
        else if (top.Score >= 0.75)
        {
            decision = SearchDecision.NeedsConfirmation;
            explanation = $"Closest match: {Label(top.Product)}. Please confirm.";
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
