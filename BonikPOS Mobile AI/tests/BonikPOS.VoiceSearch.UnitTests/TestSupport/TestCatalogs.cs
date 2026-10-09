using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.MedicineSearch;
using BonikPOS.VoiceSearch.MedicineSearch.Catalog;

namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>Small purpose-built catalogs. Ids are synthetic.</summary>
public static class TestCatalogs
{
    public static MedicineProduct P(string id, string brand, string? strength, string? form, string? generic = null,
        string? localName = null, string? manufacturer = null, string? packSize = null, IReadOnlyList<string>? aliases = null) => new()
    {
        ProductId = id,
        BrandName = brand,
        Strength = strength,
        DosageForm = form,
        GenericName = generic,
        LocalName = localName,
        Manufacturer = manufacturer,
        PackSize = packSize,
        Aliases = aliases ?? [],
    };

    public static IReadOnlyList<MedicineProduct> Small { get; } =
    [
        P("T-NAPA-500", "Napa", "500 mg", "Tablet", "Paracetamol", "নাপা"),
        P("T-NAPA-SUSP", "Napa", "120 mg/5 ml", "Suspension", "Paracetamol", "নাপা"),
        P("T-NAPA-EXTRA", "Napa Extra", "500 mg + 65 mg", "Tablet", "Paracetamol + Caffeine", "নাপা এক্সট্রা"),
        P("T-SECLO-20", "Seclo", "20 mg", "Capsule", "Omeprazole", "সেকলো"),
        P("T-SECLO-40", "Seclo", "40 mg", "Capsule", "Omeprazole", "সেকলো"),
        P("T-FEXO-120", "Fexo", "120 mg", "Tablet", "Fexofenadine"),
        P("T-MONTAIR-10", "Montair", "10 mg", "Tablet", "Montelukast"),
    ];

    public static async Task<MedicineSearchService> SearchServiceAsync(IReadOnlyList<MedicineProduct>? products = null)
    {
        var service = new MedicineSearchService(() => new InMemoryCatalogSource(products ?? Small, "unit-test catalog"));
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        return service;
    }
}
