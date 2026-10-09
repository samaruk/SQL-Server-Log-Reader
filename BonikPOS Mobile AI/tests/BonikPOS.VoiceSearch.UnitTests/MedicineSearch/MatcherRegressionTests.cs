using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.MedicineSearch.Text;
using static BonikPOS.VoiceSearch.UnitTests.TestSupport.TestCatalogs;

namespace BonikPOS.VoiceSearch.UnitTests.MedicineSearch;

/// <summary>
/// One test per matcher defect found while evaluating search on labelled data (docs/SEARCH_EVALUATION.md): each case
/// failed before the corresponding fix. Catalog entries are synthetic.
/// </summary>
public sealed class MatcherRegressionTests
{
    private static readonly IReadOnlyList<MedicineProduct> Catalog =
    [
        .. Small,
        P("R-HELLON-5", "Hellon", "5 mg", "Tablet", "Testamine"),               // sounds like a greeting
        P("R-KHOBRA-10", "Khobra", "10 mg", "Tablet", "Testazole"),             // sounds like "khobor" (news)
        P("R-SURE-20", "Sure", "20 mg", "Capsule", "Testoprazole"),             // a brand that is also an everyday word
        P("R-NAPA-EXTEND-665", "Napa Extend", "665 mg", "Tablet", "Paracetamol"),
        P("R-DAPLI-10", "Dapli", "10 mg", "Tablet", "Dapagliflozin"),
        P("R-DAPLY-10", "Daply", "10 mg", "Capsule", "Testagliflozin"),
        P("R-ZIMAX-SUSP", "Zimax", "200 mg/5 ml", "Powder for Suspension", "Azithromycin"),
        P("R-CEEVIT-250", "Ceevit", "250 mg", "Chewable Tablet", "Vitamin C (Ascorbic Acid)"),
        P("R-TRUCHI-OINT", "Truchi", "0.1%", "Ointment", "Tacrolimus"),
    ];

    private static async Task<MedicineSearchResult> SearchAsync(
        string text, IReadOnlyList<string>? alternatives = null, IReadOnlyList<MedicineProduct>? catalog = null)
    {
        var service = await SearchServiceAsync(catalog ?? Catalog);
        return await service.SearchAsync(
            new MedicineQuery(text, QuerySource.Voice) { Alternatives = alternatives ?? [] }, TestContext.Current.CancellationToken);
    }

    private static bool IsSafe(SearchDecision d) => d is SearchDecision.NoMatch or SearchDecision.LowConfidence;

    [Theory]
    [InlineData("hello")]
    [InlineData("ki khobor")]
    [InlineData("thank you very much")]
    [InlineData("ok sir")]
    public async Task EverydayConversation_IsNeverOfferedAsAMedicine(string overheard)
    {
        var r = await SearchAsync(overheard);

        Assert.True(IsSafe(r.Decision), $"\"{overheard}\" -> {r.Decision}: {r.Explanation}");
        Assert.Empty(r.Clarifications);
    }

    [Fact]
    public async Task ABrandSpelledLikeAnEverydayWord_IsStillFoundWhenSaidExactly()
    {
        var r = await SearchAsync("sure 20 mg capsule");

        Assert.Equal("R-SURE-20", r.Candidates[0].Product.ProductId);
        Assert.Equal(SearchDecision.Confident, r.Decision);
    }

    [Fact]
    public async Task FullerSpokenName_IsNotReplacedByTheShorterBrandInsideIt()
    {
        // "Napa Extend" exists (665 mg) and so does "Napa" 500 mg: "napa extend 500" must not become Napa 500.
        var r = await SearchAsync("napa extend 500");

        Assert.Equal(SearchDecision.LowConfidence, r.Decision);
        Assert.Contains("Napa Extend", r.Explanation);
    }

    [Fact]
    public async Task ExactlySpokenName_IsNotReplacedByASoundAlikeThatHasTheRequestedForm()
    {
        // Dapli exists only as a tablet; Daply (a different product) is a capsule.
        var r = await SearchAsync("dapli capsule");

        Assert.Equal(SearchDecision.LowConfidence, r.Decision);
        Assert.Contains("Dapli", r.Explanation);
        Assert.NotEqual(SearchDecision.Confident, r.Decision);
    }

    [Fact]
    public async Task MatchFoundOnlyThroughAnotherRecogniserReading_IsNeverConfident()
    {
        // The recogniser's first reading names nothing in the catalog; its second reading does.
        var r = await SearchAsync("make sure pro forty", alternatives: ["maxpro forty"],
            catalog: [.. Small, P("R-MAXPRO-40", "Maxpro", "40 mg", "Tablet", "Esomeprazole")]);

        Assert.Equal("R-MAXPRO-40", r.Candidates[0].Product.ProductId);
        Assert.Equal("maxpro forty", r.Candidates[0].MatchedReading);
        Assert.Equal(SearchDecision.NeedsConfirmation, r.Decision);
        Assert.Contains("another reading", r.Explanation);
    }

    [Theory]
    [InlineData("zimax powder")]
    [InlineData("zimax suspension")]
    public async Task EveryFormNamedInTheCatalogEntry_CountsAsTheRequestedForm(string query)
    {
        var r = await SearchAsync(query);

        Assert.Equal("R-ZIMAX-SUSP", r.Candidates[0].Product.ProductId);
        Assert.Equal(AttributeMatch.Match, r.Candidates[0].FormMatch);
        Assert.Equal(SearchDecision.Confident, r.Decision);
    }

    [Fact]
    public async Task CreamAndOintment_AreDifferentForms_SoAnOintmentIsNotOfferedAsTheRequestedCream()
    {
        var r = await SearchAsync("truchi cream");

        Assert.Equal("R-TRUCHI-OINT", r.Candidates[0].Product.ProductId);
        Assert.Equal(AttributeMatch.Mismatch, r.Candidates[0].FormMatch);
        Assert.Equal(SearchDecision.LowConfidence, r.Decision);
    }

    [Theory]
    [InlineData("ascorbic acid", "R-CEEVIT-250")]  // synonym in parentheses of the generic name
    [InlineData("vitamin c", "R-CEEVIT-250")]
    [InlineData("caffeine", "T-NAPA-EXTRA")]       // one component of a combination generic
    public async Task GenericSynonymsAndComponents_FindTheProduct(string query, string expectedId)
    {
        var r = await SearchAsync(query);

        Assert.Equal(expectedId, r.Candidates[0].Product.ProductId);
        Assert.Equal("generic", r.Candidates[0].MatchedField);
    }

    [Fact]
    public async Task BanglaSpellingOfAnEnglishBrand_IsFoundThroughTransliteration()
    {
        // The word-initial consonant cluster keeps its inherent vowel ("mon-te-yar"), which matches "Montair".
        var r = await SearchAsync("মন্টেয়ার");

        Assert.Equal("T-MONTAIR-10", r.Candidates[0].Product.ProductId);
        Assert.NotEqual(SearchDecision.NoMatch, r.Decision);
    }

    [Fact]
    public void TheWordOh_IsNotReadAsTheNumberZero()
    {
        var parsed = QueryParser.Parse("fax oh");

        Assert.Empty(parsed.Strengths);
        Assert.Empty(parsed.BareNumbers);
    }

    [Theory]
    [InlineData("zentel 400")]   // a brand that is not in this catalog and sounds like nothing in it
    [InlineData("histacin 4 mg")]
    public async Task BrandMissingFromTheCatalog_IsNeverReplacedByAnotherProduct(string query)
    {
        var r = await SearchAsync(query);

        Assert.True(IsSafe(r.Decision), $"\"{query}\" -> {r.Decision}: {r.Explanation}");
        Assert.Empty(r.Clarifications);
    }
}
