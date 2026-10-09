using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.MedicineSearch.Ranking;
using BonikPOS.VoiceSearch.MedicineSearch.Text;
using static BonikPOS.VoiceSearch.UnitTests.TestSupport.TestCatalogs;

namespace BonikPOS.VoiceSearch.UnitTests.MedicineSearch;

/// <summary>
/// The decision policy on hand-made candidate lists, so every rule is exercised in isolation:
/// close candidates are never decided on the owner's behalf, mismatched strength/form is never "the product", and
/// upstream uncertainty (speech recognition, speaker) caps the decision.
/// </summary>
public sealed class SearchDecisionPolicyTests
{
    private static readonly SearchThresholds Thresholds = new();

    private static MedicineCandidate Candidate(MedicineProduct p, double score, MatchKind kind = MatchKind.Exact,
        AttributeMatch strength = AttributeMatch.NotRequested, AttributeMatch form = AttributeMatch.NotRequested, string field = "brand") =>
        new(p, score, score, kind, field, strength, form, []);

    private static (SearchDecision Decision, IReadOnlyList<ClarificationOption> Options, string Explanation) Decide(
        IReadOnlyList<MedicineCandidate> ranked, double? recognition = null, OwnerVoiceDecision? speaker = null, string text = "seclo 20 mg") =>
        SearchDecisionPolicy.Decide(new MedicineQuery(text, QuerySource.Voice, recognition, speaker), QueryParser.Parse(text), ranked, Thresholds);

    private static readonly MedicineProduct Seclo20 = P("S20", "Seclo", "20 mg", "Capsule");
    private static readonly MedicineProduct Seclo40 = P("S40", "Seclo", "40 mg", "Capsule");
    private static readonly MedicineProduct Sergel = P("SG20", "Sergel", "20 mg", "Capsule");

    private static IReadOnlyList<MedicineCandidate> ClearWinner => [Candidate(Seclo20, 1.0, strength: AttributeMatch.Match), Candidate(Seclo40, 0.75)];

    private static IReadOnlyList<MedicineCandidate> TwoStrengths => [Candidate(Seclo20, 1.0), Candidate(Seclo40, 1.0)];

    [Fact]
    public void ClearExactWinner_IsConfident()
    {
        var (decision, options, explanation) = Decide(ClearWinner);
        Assert.Equal(SearchDecision.Confident, decision);
        Assert.Empty(options);
        Assert.Equal("Matched Seclo 20 mg Capsule.", explanation);
    }

    [Fact]
    public void CandidatesWithinTheAmbiguityMargin_AreAmbiguous_WithOneOptionEach()
    {
        var (decision, options, _) = Decide([Candidate(Seclo20, 0.97), Candidate(Seclo40, 0.92), Candidate(Sergel, 0.80)]);

        Assert.Equal(SearchDecision.Ambiguous, decision);
        Assert.Equal(["S20", "S40"], options.Select(o => o.ProductId));
        Assert.Equal(["Seclo 20 mg Capsule", "Seclo 40 mg Capsule"], options.Select(o => o.Label));
    }

    [Fact]
    public void FuzzyOrWeakTopCandidates_AreNotConfident()
    {
        Assert.Equal(SearchDecision.NeedsConfirmation, Decide([Candidate(Seclo20, 0.95, MatchKind.Fuzzy)]).Decision);
        Assert.Equal(SearchDecision.NeedsConfirmation, Decide([Candidate(Seclo20, 0.85)]).Decision);
        Assert.Equal(SearchDecision.LowConfidence, Decide([Candidate(Seclo20, 0.6, MatchKind.Fuzzy)]).Decision);
        Assert.Equal(SearchDecision.NoMatch, Decide([]).Decision);
    }

    [Theory]
    [InlineData(AttributeMatch.Mismatch, AttributeMatch.NotRequested, "requested strength")]
    [InlineData(AttributeMatch.Match, AttributeMatch.Mismatch, "requested form")]
    public void MismatchedStrengthOrForm_IsAlwaysLowConfidence(AttributeMatch strength, AttributeMatch form, string expected)
    {
        var (decision, _, explanation) = Decide([Candidate(Seclo40, 1.0, strength: strength, form: form)], recognition: 0.99, speaker: OwnerVoiceDecision.Owner);

        Assert.Equal(SearchDecision.LowConfidence, decision);
        Assert.Contains(expected, explanation);
    }

    [Theory]
    [InlineData(0.44)]
    [InlineData(0.1)]
    public void LowRecognitionConfidence_CapsTheDecisionAtLowConfidence(double recognition)
    {
        var confident = Decide(ClearWinner, recognition);
        Assert.Equal(SearchDecision.LowConfidence, confident.Decision);
        Assert.StartsWith("Speech recognition was not confident.", confident.Explanation);

        Assert.Equal(SearchDecision.LowConfidence, Decide([Candidate(Seclo20, 0.85)], recognition).Decision);

        // "At best LowConfidence" (SearchThresholds.MinRecognitionConfidence) also covers an ambiguous result.
        var ambiguous = Decide(TwoStrengths, recognition, text: "seclo");
        Assert.Equal(SearchDecision.LowConfidence, ambiguous.Decision);
        Assert.StartsWith("Speech recognition was not confident.", ambiguous.Explanation);
        Assert.Empty(ambiguous.Options);
    }

    [Fact]
    public void RecognitionConfidenceAtTheThreshold_OrTypedQueries_AreNotCapped()
    {
        Assert.Equal(SearchDecision.Confident, Decide(ClearWinner, recognition: Thresholds.MinRecognitionConfidence).Decision);
        Assert.Equal(SearchDecision.Confident, Decide(ClearWinner, recognition: null).Decision);
        Assert.Equal(SearchDecision.Ambiguous, Decide(TwoStrengths, recognition: 0.9, text: "seclo").Decision);
    }

    [Theory]
    [InlineData(OwnerVoiceDecision.Uncertain)]
    [InlineData(OwnerVoiceDecision.NotOwner)]
    [InlineData(OwnerVoiceDecision.NoProfile)]
    [InlineData(OwnerVoiceDecision.ModelUnavailable)]
    [InlineData(OwnerVoiceDecision.InsufficientSpeech)]
    public void SpeakerNotConfirmedAsTheOwner_IsNeverConfident(OwnerVoiceDecision speaker)
    {
        var (decision, _, explanation) = Decide(ClearWinner, recognition: 0.95, speaker: speaker);

        Assert.Equal(SearchDecision.NeedsConfirmation, decision);
        Assert.StartsWith("The owner's voice could not be confirmed.", explanation);
    }

    [Fact]
    public void ConfirmedOwner_KeepsAConfidentDecision_AndOtherDecisionsAreNotUpgraded()
    {
        Assert.Equal(SearchDecision.Confident, Decide(ClearWinner, 0.95, OwnerVoiceDecision.Owner).Decision);
        Assert.Equal(SearchDecision.Ambiguous, Decide(TwoStrengths, 0.95, OwnerVoiceDecision.Uncertain, "seclo").Decision);
        Assert.Equal(SearchDecision.LowConfidence,
            Decide([Candidate(Seclo40, 1.0, strength: AttributeMatch.Mismatch)], 0.95, OwnerVoiceDecision.Uncertain).Decision);
    }
}
