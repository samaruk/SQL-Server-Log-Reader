using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.MedicineSearch;
using BonikPOS.VoiceSearch.MedicineSearch.Catalog;
using BonikPOS.VoiceSearch.MedicineSearch.Ranking;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;
using static BonikPOS.VoiceSearch.UnitTests.TestSupport.TestCatalogs;

namespace BonikPOS.VoiceSearch.UnitTests.MedicineSearch;

public sealed class RankingTests
{
    private static async Task<MedicineSearchResult> SearchAsync(string text, IReadOnlyList<MedicineProduct>? catalog = null,
        double? recognitionConfidence = null, OwnerVoiceDecision? speaker = null)
    {
        var service = await SearchServiceAsync(catalog);
        return await service.SearchAsync(new MedicineQuery(text, QuerySource.Typed, recognitionConfidence, speaker), TestContext.Current.CancellationToken);
    }

    private static async Task<MedicineSearchResult> SearchDemoAsync(string text)
    {
        var service = new MedicineSearchService(() => new DemoCatalogSource());
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        return await service.SearchAsync(new MedicineQuery(text, QuerySource.Typed), TestContext.Current.CancellationToken);
    }

    private static string TopId(MedicineSearchResult r) => r.Candidates[0].Product.ProductId;

    [Theory]
    [InlineData("seclo 20 mg", "T-SECLO-20")]
    [InlineData("Seclo 40mg capsule", "T-SECLO-40")]
    [InlineData("napa extra", "T-NAPA-EXTRA")]       // the longer exact brand beats the shorter brand inside it
    [InlineData("fexo", "T-FEXO-120")]
    [InlineData("সেকলো ২০ মিলিগ্রাম", "T-SECLO-20")]  // Bangla local name
    [InlineData("seklo 20 mg", "T-SECLO-20")]        // Banglish spelling: same pronunciation
    public async Task ExactOrSamePronunciationBrand_IsRankedFirst_AndConfident(string query, string expectedId)
    {
        var r = await SearchAsync(query);

        Assert.Equal(expectedId, TopId(r));
        Assert.Equal(SearchDecision.Confident, r.Decision);
        Assert.True(r.Candidates[0].NameMatch >= MatchKind.ExactPhonetic);
        Assert.Empty(r.Clarifications);
    }

    [Fact]
    public async Task SameBrandInSeveralStrengths_IsAmbiguous_UntilAStrengthIsGiven()
    {
        var r = await SearchAsync("seclo");

        Assert.Equal(SearchDecision.Ambiguous, r.Decision);
        Assert.Equal(["T-SECLO-20", "T-SECLO-40"], r.Clarifications.Select(c => c.ProductId).Order());
        Assert.Contains("Seclo is available in 2 strengths", r.Explanation);
        Assert.Contains(r.Clarifications, c => c.Label == "Seclo 20 mg Capsule");

        foreach (var (query, id) in new[] { ("seclo 40", "T-SECLO-40"), ("seclo 40 mg", "T-SECLO-40"), ("seclo twenty", "T-SECLO-20") })
        {
            var specific = await SearchAsync(query);
            Assert.Equal(SearchDecision.Confident, specific.Decision);
            Assert.Equal(id, TopId(specific));
            Assert.Equal(AttributeMatch.Match, specific.Candidates[0].StrengthMatch);
        }
    }

    [Theory]
    [InlineData("seclo 30 mg", "30 mg")]
    [InlineData("seclo thirty milligram", "30 mg")]
    [InlineData("napa 1 g", "1000 mg")]
    public async Task RequestedStrengthThatDoesNotExist_IsLowConfidence_WithAMismatchNote(string query, string asked)
    {
        var r = await SearchAsync(query);

        Assert.Equal(SearchDecision.LowConfidence, r.Decision);
        Assert.NotEmpty(r.Candidates);
        Assert.All(r.Candidates.Where(c => c.Product.BrandName is "Seclo" or "Napa"), c => Assert.Equal(AttributeMatch.Mismatch, c.StrengthMatch));
        Assert.Contains("with the requested strength was found", r.Explanation);
        Assert.Contains(r.Candidates[0].Notes, n => n.Contains("Different strength") && n.Contains(asked));
    }

    [Fact]
    public async Task RequestedStrengthThatDoesNotExist_IsNeverConfident_EvenForAPerfectVoiceQuery()
    {
        var r = await SearchAsync("seclo 30 mg", recognitionConfidence: 0.99, speaker: OwnerVoiceDecision.Owner);
        Assert.Equal(SearchDecision.LowConfidence, r.Decision);

        var bare = await SearchAsync("seclo 30");
        Assert.NotEqual(SearchDecision.Confident, bare.Decision);
        Assert.Equal(AttributeMatch.Mismatch, bare.Candidates[0].StrengthMatch);
    }

    [Fact]
    public async Task DosageForm_SelectsBetweenProductsOfOneBrand()
    {
        var suspension = await SearchAsync("napa suspension");
        Assert.Equal("T-NAPA-SUSP", TopId(suspension));
        Assert.Equal(AttributeMatch.Match, suspension.Candidates[0].FormMatch);
        Assert.Equal(SearchDecision.Confident, suspension.Decision);

        // "syrup" is what owners commonly say for an oral suspension: it selects the suspension, but a compatible form is
        // not the listed one, so it needs confirmation and is never Confident (AttributeMatch.Compatible).
        var syrup = await SearchAsync("napa syrup");
        Assert.Equal("T-NAPA-SUSP", TopId(syrup));
        Assert.Equal(AttributeMatch.Compatible, syrup.Candidates[0].FormMatch);
        Assert.Contains(syrup.Candidates[0].Notes, n => n.Contains("Listed as Suspension"));
        Assert.Equal(SearchDecision.NeedsConfirmation, syrup.Decision);
        Assert.Equal(AttributeMatch.Mismatch, syrup.Candidates.Single(c => c.Product.ProductId == "T-NAPA-500").FormMatch);

        var tablet = await SearchAsync("napa tablet");
        Assert.Equal("T-NAPA-500", TopId(tablet));
        Assert.Equal(SearchDecision.Confident, tablet.Decision);

        var injection = await SearchAsync("napa injection");
        Assert.Equal(SearchDecision.LowConfidence, injection.Decision);
        Assert.Contains("requested form", injection.Explanation);
    }

    [Fact]
    public async Task GenericName_FindsEveryBrandOfThatMolecule_AndAsksForTheBrand()
    {
        var r = await SearchDemoAsync("omeprazole");

        Assert.Equal(SearchDecision.Ambiguous, r.Decision);
        Assert.Equal(["DEMO-0010", "DEMO-0011", "DEMO-0012"], r.Clarifications.Select(c => c.ProductId).Order());
        Assert.All(r.Candidates.Take(3), c => Assert.Equal("generic", c.MatchedField));
        Assert.Contains("3 products contain Omeprazole", r.Explanation);
        Assert.Contains(r.Candidates[0].Notes, n => n.Contains("generic name Omeprazole"));
    }

    [Fact]
    public async Task GenericName_ScoresBelowAnExactBrand()
    {
        // "Fexofenadine" matches T-FEXO-120 through its generic name only; a brand hit would score 1.0.
        var r = await SearchAsync("fexofenadine");

        Assert.Equal("T-FEXO-120", TopId(r));
        Assert.Equal("generic", r.Candidates[0].MatchedField);
        Assert.True(r.Candidates[0].Score < 1.0);
    }

    [Theory]
    [InlineData("umbrella")]
    [InlineData("table chair")]
    [InlineData("bicycle")]
    [InlineData("good morning")]
    public async Task UnrelatedWords_AreNoMatch(string query)
    {
        var r = await SearchDemoAsync(query);

        Assert.Equal(SearchDecision.NoMatch, r.Decision);
        Assert.Empty(r.Candidates);
        Assert.Empty(r.Clarifications);
    }

    [Fact]
    public async Task NoMatch_OffersNearMissCatalogNames_WithTheSpokenStrengthAndForm_WithoutSelectingAnything()
    {
        var r = await SearchAsync("fekzoxyl 120 mg tablet");

        Assert.Equal(SearchDecision.NoMatch, r.Decision);
        Assert.Empty(r.Candidates);            // a suggestion is not a match: nothing is offered for confirmation
        Assert.Empty(r.Clarifications);
        var suggestion = Assert.Single(r.Suggestions);
        Assert.Equal("Fexo", suggestion.CatalogName);
        Assert.Equal("Fexo 120 mg tablet", suggestion.QueryText);   // the catalog spelling plus the strength and form said
        Assert.InRange(suggestion.Similarity, new SearchThresholds().SuggestionMinNameScore, 1.0);

        var unrelated = await SearchAsync("bicycle");
        Assert.Equal(SearchDecision.NoMatch, unrelated.Decision);
        Assert.Empty(unrelated.Suggestions);   // nothing similar enough in the catalog
    }

    [Fact]
    public async Task Suggestions_AreDistinctCatalogNames_ClosestFirst_AtMostThree_AboveTheSimilarityThreshold()
    {
        IReadOnlyList<MedicineProduct> catalog =
        [
            P("S-FEXO-60", "Fexo", "60 mg", "Tablet"),
            P("S-FEXO-120", "Fexo", "120 mg", "Tablet"),
            P("S-FEXA", "Fexa", "10 mg", "Tablet"),
            P("S-FEXI", "Fexi", "10 mg", "Tablet"),
            P("S-FEXU", "Fexu", "10 mg", "Tablet"),
            P("S-NAPA", "Napa", "500 mg", "Tablet"),
        ];
        var service = await SearchServiceAsync(catalog);
        // A strict candidate cut-off turns these near names into NoMatch, the decision that carries suggestions.
        service.Thresholds = new SearchThresholds { MinCandidateScore = 0.99 };
        var query = new MedicineQuery("fexoz 10 mg", QuerySource.Typed);

        var r = await service.SearchAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(SearchDecision.NoMatch, r.Decision);
        Assert.Equal(MedicineMatcher.MaxSuggestions, r.Suggestions.Count);   // four similar names, three offered
        // Fexo has two products but is suggested once; equal similarities are ordered by name, so the list is deterministic.
        Assert.Equal(["Fexo", "Fexa", "Fexi"], r.Suggestions.Select(s => s.CatalogName));
        Assert.True(r.Suggestions[0].Similarity > r.Suggestions[1].Similarity);
        Assert.All(r.Suggestions, s => Assert.Equal(s.CatalogName + " 10 mg", s.QueryText));

        service.Thresholds = new SearchThresholds { MinCandidateScore = 0.99, SuggestionMinNameScore = 0.95 };
        var strict = await service.SearchAsync(query, TestContext.Current.CancellationToken);
        Assert.Equal(SearchDecision.NoMatch, strict.Decision);
        Assert.Empty(strict.Suggestions);
    }

    [Fact]
    public async Task QueryWithoutAMedicineName_IsNoMatch()
    {
        var r = await SearchAsync("please give me two strips");

        Assert.Equal(SearchDecision.NoMatch, r.Decision);
        Assert.Contains("No medicine name", r.Explanation);
    }

    [Fact]
    public async Task SimilarOrIdenticalNames_WithDifferentIds_AreNeverMerged_AndKeepTheirCatalogValuesVerbatim()
    {
        var a1 = P("A-1", "Napa", "500mg", "Tab.", "Paracetamol", manufacturer: "Maker One", packSize: "10x10");
        var a2 = P("A-2", "Napa", "500 mg", "Tablet", "Paracetamol", manufacturer: "Maker Two", packSize: "1x12");
        var a3 = P("A-3", "NAPA", "500 MG", "tablet");
        var a4 = P("A-4", "Napaa", "500 mg", "Tablet");
        var duplicate = P("A-1", "Napa Duplicate Row", "650 mg", "Tablet");
        var catalog = new[] { a1, a2, a3, a4, duplicate };

        var service = await SearchServiceAsync(catalog);
        var r = await service.SearchAsync(new MedicineQuery("napa 500 mg tablet", QuerySource.Typed), TestContext.Current.CancellationToken);

        Assert.Equal(4, service.CatalogInfo.ProductCount);
        Assert.Contains(service.CatalogInfo.Warnings, w => w.Contains("Duplicate product id 'A-1'"));
        Assert.Same(a1, service.GetProduct("A-1"));                       // the first row wins; nothing is merged into it

        var ids = r.Candidates.Select(c => c.Product.ProductId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(["A-1", "A-2", "A-3", "A-4"], ids.Order());
        foreach (var (expected, candidate) in new[] { a1, a2, a3, a4 }.Select(p => (p, r.Candidates.Single(c => c.Product.ProductId == p.ProductId))))
        {
            Assert.Same(expected, candidate.Product); // the catalog record itself: strength, form, maker, pack untouched
        }

        Assert.Equal("500mg", r.Candidates.Single(c => c.Product.ProductId == "A-1").Product.Strength);
        Assert.Equal("Tab.", r.Candidates.Single(c => c.Product.ProductId == "A-1").Product.DosageForm);
        Assert.Equal("500 MG", r.Candidates.Single(c => c.Product.ProductId == "A-3").Product.Strength);
        Assert.DoesNotContain(r.Candidates, c => c.Product.BrandName == "Napa Duplicate Row");

        Assert.Equal(SearchDecision.Ambiguous, r.Decision);   // the system never picks one of them on the owner's behalf
        Assert.Equal(["A-1", "A-2", "A-3", "A-4"], r.Clarifications.Select(c => c.ProductId).Order());
        Assert.Contains(r.Clarifications, c => c.Label == "Napa 500mg Tab.");
    }

    [Fact]
    public async Task BrandContainingDigits_IsFoundWithTheNumberSpoken()
    {
        var r = await SearchDemoAsync("cef 3");

        Assert.Equal("DEMO-0036", TopId(r));
        Assert.Equal(MatchKind.Exact, r.Candidates[0].NameMatch);
        Assert.NotEqual(AttributeMatch.Mismatch, r.Candidates[0].StrengthMatch); // "3" belongs to the brand, not a strength
    }

    [Fact]
    public async Task DemoCatalog_IsLabelledAsDemoData()
    {
        var service = new MedicineSearchService(() => new DemoCatalogSource());
        await service.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(service.CatalogInfo.IsLoaded);
        Assert.True(service.CatalogInfo.IsDemoData);
        Assert.Equal(55, service.CatalogInfo.ProductCount);
        Assert.Contains("DEMO", service.CatalogInfo.SourceDescription);
        Assert.Equal("Napa Extra", service.GetProduct("DEMO-0004")!.BrandName);
    }

    [Fact]
    public async Task SearchBeforeTheCatalogIsLoaded_FailsClearly()
    {
        var service = new MedicineSearchService(() => new InMemoryCatalogSource([], "empty"));
        await Assert.ThrowsAsync<CatalogUnavailableException>(() =>
            service.SearchAsync(new MedicineQuery("napa", QuerySource.Typed), TestContext.Current.CancellationToken));
        Assert.False(service.CatalogInfo.IsLoaded);
    }

    [Fact]
    public async Task LearnedProductsAndPopularity_RefineRanking_ButNeverBypassTheChoice()
    {
        var hints = new FakeHints(learned: new() { ["seclo"] = ["T-SECLO-40"] }, popularity: new() { ["T-SECLO-40"] = 5 });
        var service = new MedicineSearchService(() => new InMemoryCatalogSource(Small, "test"), hints);
        await service.InitializeAsync(TestContext.Current.CancellationToken);

        var r = await service.SearchAsync(new MedicineQuery("Seclo", QuerySource.Typed), TestContext.Current.CancellationToken);

        Assert.Equal("T-SECLO-40", TopId(r));                                     // popularity breaks the tie
        Assert.Contains(r.Candidates[0].Notes, n => n.Contains("chose this product for the same words before"));
        Assert.Equal(SearchDecision.Ambiguous, r.Decision);                       // still the owner's decision
    }

    [Fact]
    public async Task Thresholds_LimitTheCandidateList()
    {
        var service = new MedicineSearchService(() => new DemoCatalogSource());
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        service.Thresholds = new SearchThresholds { MaxCandidates = 3 };

        var r = await service.SearchAsync(new MedicineQuery("paracetamol", QuerySource.Typed), TestContext.Current.CancellationToken);

        Assert.Equal(3, r.Candidates.Count);
        Assert.All(r.Candidates, c => Assert.True(c.Score >= service.Thresholds.MinCandidateScore));
    }

    private sealed class FakeHints(Dictionary<string, IReadOnlyList<string>> learned, Dictionary<string, int> popularity) : IMedicineSearchHints
    {
        public int GetPopularity(string productId) => popularity.GetValueOrDefault(productId);

        public IReadOnlyList<string> GetLearnedProducts(string normalizedQuery) => learned.GetValueOrDefault(normalizedQuery) ?? [];
    }
}
