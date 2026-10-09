using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Presentation.Services;

namespace BonikPOS.VoiceSearch.Presentation.ViewModels;

/// <summary>One ranked product. Every value shown is the catalog's own value for that product id.</summary>
public sealed class ResultItemViewModel(MedicineCandidate candidate, int rank)
{
    public MedicineCandidate Candidate { get; } = candidate;

    public int Rank { get; } = rank;

    public bool IsTop => Rank == 1;

    public string ProductId => Candidate.Product.ProductId;

    public string BrandName => Candidate.Product.BrandName;

    public string GenericName => Candidate.Product.GenericName ?? "Generic name not in catalog";

    public string Strength => Candidate.Product.Strength ?? "Strength not in catalog";

    public string DosageForm => Candidate.Product.DosageForm ?? "Form not in catalog";

    public string Details => $"{Strength} · {DosageForm}";

    public string MatchText => $"{UserMessages.Describe(Candidate.NameMatch)} · {Candidate.Score:P0}";

    public bool HasWarning => Candidate.StrengthMatch == AttributeMatch.Mismatch || Candidate.FormMatch == AttributeMatch.Mismatch;

    public string Notes => string.Join(" ", Candidate.Notes);

    public bool HasNotes => Candidate.Notes.Count > 0;

    public string AccessibleDescription =>
        $"Result {Rank}: {BrandName}, {GenericName}, {Strength}, {DosageForm}, product id {ProductId}. {MatchText}. {Notes}";
}
