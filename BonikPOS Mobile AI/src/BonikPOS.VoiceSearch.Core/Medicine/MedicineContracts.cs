using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Core.Medicine;

/// <summary>
/// A product exactly as the catalog source returned it. Values are never rewritten or merged; the index keeps its
/// own normalised copies for matching.
/// </summary>
public sealed record MedicineProduct
{
    public required string ProductId { get; init; }

    public required string BrandName { get; init; }

    public string? GenericName { get; init; }

    /// <summary>Strength as written in the catalog, e.g. "500 mg", "120 mg/5 ml".</summary>
    public string? Strength { get; init; }

    /// <summary>Dosage form as written in the catalog, e.g. "Tablet", "Syrup".</summary>
    public string? DosageForm { get; init; }

    public string? Manufacturer { get; init; }

    public string? PackSize { get; init; }

    /// <summary>Optional local-script name (for example the Bangla spelling of the brand).</summary>
    public string? LocalName { get; init; }

    /// <summary>Optional alternative names known to the catalog.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public enum DosageFormCategory
{
    Unknown,
    Tablet,
    Capsule,
    Syrup,
    Suspension,
    Drops,
    Injection,
    Infusion,
    Cream,
    Ointment,
    Gel,
    Lotion,
    Inhaler,
    Powder,
    Suppository,
    Solution,
    Spray,
    Other,
}

public enum StrengthUnit
{
    Milligram,
    Microgram,
    Gram,
    Millilitre,
    InternationalUnit,
    Percent,
}

/// <summary>One strength component (a combination product has several). Amounts are normalised (g -> mg, mcg kept).</summary>
public readonly record struct StrengthValue(decimal Amount, StrengthUnit Unit)
{
    public override string ToString() => Unit switch
    {
        StrengthUnit.Milligram => $"{Amount:0.###} mg",
        StrengthUnit.Microgram => $"{Amount:0.###} mcg",
        StrengthUnit.Gram => $"{Amount:0.###} g",
        StrengthUnit.Millilitre => $"{Amount:0.###} ml",
        StrengthUnit.InternationalUnit => $"{Amount:0.###} IU",
        StrengthUnit.Percent => $"{Amount:0.###}%",
        _ => Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

public enum QuerySource
{
    Voice,
    Typed,
    Correction,
}

public sealed record MedicineQuery(
    string Text,
    QuerySource Source,
    double? RecognitionConfidence = null,
    OwnerVoiceDecision? SpeakerDecision = null)
{
    /// <summary>
    /// Other readings of the same speech from the recogniser (n-best, or the other language when Bangla/English
    /// detection was uncertain). They are searched as correction candidates; <see cref="Text"/> stays the primary reading.
    /// </summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];
}

/// <summary>What the query parser understood. Shown to the owner so that the interpretation is transparent.</summary>
/// <param name="NameWithNumbersText">Name tokens with the bare numbers kept in place, for brands that contain digits ("Cef-3").</param>
/// <param name="NameWithFormsText">
/// Name tokens with dosage-form words kept in place, for names that a recogniser split into a form word ("sir gel" for
/// "Sergel"). A form word used as part of a matched name is not treated as a requested form for that product.
/// </param>
public sealed record ParsedMedicineQuery(
    string OriginalText,
    string NormalizedText,
    string NameText,
    string NamePhoneticKey,
    IReadOnlyList<StrengthValue> Strengths,
    IReadOnlyList<decimal> BareNumbers,
    IReadOnlyList<DosageFormCategory> Forms,
    bool ContainsBanglaScript,
    string NameWithNumbersText = "",
    string NameWithFormsText = "");

public enum MatchKind
{
    None,
    Fuzzy,
    Prefix,
    ExactPhonetic,
    Exact,
}

public enum AttributeMatch
{
    /// <summary>The query did not mention this attribute.</summary>
    NotRequested,
    Match,
    /// <summary>A form the owner commonly uses for this one (e.g. "syrup" for an oral suspension); needs confirmation.</summary>
    Compatible,
    Mismatch,
    /// <summary>The query mentioned it but the catalog entry has no parsable value.</summary>
    Unknown,
}

/// <param name="MatchedReading">Set when the product matched an alternative recogniser reading rather than the primary text.</param>
/// <param name="NameCoverage">Share of the spoken name words explained by the matched name (1 = all of them).</param>
public sealed record MedicineCandidate(
    MedicineProduct Product,
    double Score,
    double NameScore,
    MatchKind NameMatch,
    string MatchedField,
    AttributeMatch StrengthMatch,
    AttributeMatch FormMatch,
    IReadOnlyList<string> Notes,
    string? MatchedReading = null,
    double NameCoverage = 1.0);

public enum SearchDecision
{
    /// <summary>One clear match. The owner still confirms it; nothing is selected automatically.</summary>
    Confident,
    /// <summary>A plausible best match whose evidence is not strong enough to treat as confident.</summary>
    NeedsConfirmation,
    /// <summary>Several products fit (e.g. same brand, different strengths) - ask the owner to choose.</summary>
    Ambiguous,
    /// <summary>Recognition or matching confidence is insufficient; show candidates with a warning only.</summary>
    LowConfidence,
    NoMatch,
}

/// <summary>One choice offered on the clarification screen.</summary>
public sealed record ClarificationOption(string Label, string ProductId);

/// <summary>
/// A near-miss catalog name offered when nothing matched well enough. It is NOT a match: the owner must tap it to
/// search again, and must then still confirm a product.
/// </summary>
/// <param name="QueryText">Text to search if the owner picks it: the catalog name plus the strength/form they said.</param>
/// <param name="CatalogName">The catalog's own spelling of the similar name.</param>
/// <param name="Similarity">Name similarity (0..1) that produced the suggestion.</param>
public sealed record CorrectionSuggestion(string QueryText, string CatalogName, double Similarity);

public sealed record MedicineSearchResult(
    MedicineQuery Query,
    ParsedMedicineQuery Parsed,
    SearchDecision Decision,
    IReadOnlyList<MedicineCandidate> Candidates,
    IReadOnlyList<ClarificationOption> Clarifications,
    string Explanation,
    TimeSpan Elapsed,
    MedicineCatalogInfo Catalog)
{
    /// <summary>"Did you mean" names for <see cref="SearchDecision.NoMatch"/> and weak results (never auto-applied).</summary>
    public IReadOnlyList<CorrectionSuggestion> Suggestions { get; init; } = [];
}

public sealed record MedicineCatalogInfo(
    bool IsLoaded,
    int ProductCount,
    string SourceDescription,
    bool IsDemoData,
    DateTimeOffset? RetrievedAt,
    IReadOnlyList<string> Warnings)
{
    public static MedicineCatalogInfo NotLoaded { get; } = new(false, 0, "Not loaded", false, null, []);
}

/// <summary>Configurable decision thresholds (see docs/ARCHITECTURE.md, "Search decision policy").</summary>
public sealed record SearchThresholds
{
    /// <summary>Candidates scoring below this are discarded.</summary>
    public double MinCandidateScore { get; init; } = 0.55;

    /// <summary>Top score required for <see cref="SearchDecision.Confident"/>.</summary>
    public double ConfidentScore { get; init; } = 0.90;

    /// <summary>Minimum gap between the first and second candidate for a non-ambiguous decision.</summary>
    public double AmbiguityMargin { get; init; } = 0.06;

    /// <summary>Speech-recognition confidence below which the result is at best <see cref="SearchDecision.LowConfidence"/>.</summary>
    public double MinRecognitionConfidence { get; init; } = 0.45;

    /// <summary>Top score required for <see cref="SearchDecision.NeedsConfirmation"/> and for offering an
    /// <see cref="SearchDecision.Ambiguous"/> choice; weaker results are <see cref="SearchDecision.LowConfidence"/>.</summary>
    public double MinConfirmScore { get; init; } = 0.75;

    /// <summary>
    /// For approximate (fuzzy) name matches only: name similarity required before the best product is offered for
    /// confirmation or as an ambiguity choice; below it the result is <see cref="SearchDecision.LowConfidence"/>.
    /// 0 disables the check. Default 0.84 was chosen on the synthetic DEV split (smallest value with at most 3% wrong
    /// products offered on negative cases); see docs/SEARCH_EVALUATION.md before changing it.
    /// </summary>
    public double MinFuzzyConfirmNameScore { get; init; } = 0.84;

    /// <summary>Name similarity for a near-miss name to be offered as a "did you mean" suggestion on no-match.</summary>
    public double SuggestionMinNameScore { get; init; } = 0.45;

    public int MaxCandidates { get; init; } = 10;
}

/// <summary>A snapshot of products from one source.</summary>
public sealed record CatalogSnapshot(
    IReadOnlyList<MedicineProduct> Products,
    DateTimeOffset RetrievedAt,
    string SourceDescription,
    bool IsDemoData,
    IReadOnlyList<string> Warnings);

/// <summary>Where products come from: the BonikPOS backend, a local product index file, or the demo catalog.</summary>
public interface IMedicineCatalogSource
{
    string Description { get; }

    bool IsDemoData { get; }

    Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Ranked medicine search over the catalog. Exact matching first, then approximate retrieval and ranking.</summary>
public interface IMedicineSearchService
{
    MedicineCatalogInfo CatalogInfo { get; }

    SearchThresholds Thresholds { get; set; }

    /// <summary>Loads the configured catalog and builds the index.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the catalog (e.g. after a sync) and rebuilds the index.</summary>
    Task LoadCatalogAsync(IMedicineCatalogSource source, CancellationToken cancellationToken);

    Task<MedicineSearchResult> SearchAsync(MedicineQuery query, CancellationToken cancellationToken);

    MedicineProduct? GetProduct(string productId);
}

public sealed class CatalogUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Local, owner-specific signals that refine ranking: how often a product was confirmed (tie-breaking only) and
/// products the owner chose after correcting a query. Hints never bypass confirmation.
/// </summary>
public interface IMedicineSearchHints
{
    int GetPopularity(string productId);

    /// <summary>Product ids the owner confirmed for this exact normalised query text before.</summary>
    IReadOnlyList<string> GetLearnedProducts(string normalizedQuery);
}

/// <summary>Loads the catalog chosen in settings (demo, local index file or BonikPOS API) into the search service.</summary>
public interface ICatalogManager
{
    MedicineCatalogInfo Info { get; }

    Task ReloadAsync(CancellationToken cancellationToken);
}
