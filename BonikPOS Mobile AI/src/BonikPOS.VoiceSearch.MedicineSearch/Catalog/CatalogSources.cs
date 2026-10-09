using System.Reflection;
using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Medicine;

namespace BonikPOS.VoiceSearch.MedicineSearch.Catalog;

/// <summary>
/// Reads a product index in JSON: either an array of products or an object with a "products" array. Property names
/// are matched case-insensitively (productId/id, brandName/brand/name, genericName/generic, strength, dosageForm/form,
/// manufacturer/company, packSize, localName/banglaName, aliases). Values are kept verbatim.
/// </summary>
public class JsonCatalogSource(Func<Stream> open, string description, bool isDemoData) : IMedicineCatalogSource
{
    public string Description { get; } = description;

    public bool IsDemoData { get; } = isDemoData;

    public static JsonCatalogSource FromFile(string path, bool isDemoData = false) =>
        new(() => File.OpenRead(path), $"Local product index ({Path.GetFileName(path)})", isDemoData);

    public async Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await using var stream = open();
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        var root = doc.RootElement;
        JsonElement items = root.ValueKind == JsonValueKind.Array
            ? root
            : TryGet(root, "products") ?? TryGet(root, "items")
              ?? throw new CatalogUnavailableException("The catalog JSON has no product array.");
        var products = new List<MedicineProduct>(items.GetArrayLength());
        int row = 0;
        foreach (var item in items.EnumerateArray())
        {
            row++;
            var product = ReadProduct(item);
            if (product is null)
            {
                warnings.Add($"Row {row} has no product id or brand name and was skipped.");
                continue;
            }

            products.Add(product);
        }

        return new CatalogSnapshot(products, DateTimeOffset.UtcNow, Description, IsDemoData, warnings);
    }

    public static MedicineProduct? ReadProduct(JsonElement item)
    {
        string? id = Str(item, "productId", "id", "product_id", "sku");
        string? brand = Str(item, "brandName", "brand", "name", "productName", "brand_name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(brand))
        {
            return null;
        }

        var aliases = TryGet(item, "aliases") is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
        return new MedicineProduct
        {
            ProductId = id,
            BrandName = brand,
            GenericName = Str(item, "genericName", "generic", "generic_name"),
            Strength = Str(item, "strength"),
            DosageForm = Str(item, "dosageForm", "form", "dosage_form"),
            Manufacturer = Str(item, "manufacturer", "company"),
            PackSize = Str(item, "packSize", "pack_size"),
            LocalName = Str(item, "localName", "banglaName", "local_name"),
            Aliases = aliases,
        };
    }

    internal static JsonElement? TryGet(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    private static string? Str(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
        {
            if (TryGet(obj, n) is { } v)
            {
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                };
            }
        }

        return null;
    }
}

/// <summary>
/// The bundled DEMO catalog (Catalog/demo-catalog.json). Product ids are synthetic ("DEMO-xxxx"); names and strengths
/// are illustrative test data and are not an authoritative drug database. The app labels results from it as demo data.
/// </summary>
public sealed class DemoCatalogSource() : JsonCatalogSource(
    () => typeof(DemoCatalogSource).Assembly.GetManifestResourceStream("BonikPOS.VoiceSearch.MedicineSearch.demo-catalog.json")
          ?? throw new CatalogUnavailableException("Demo catalog resource is missing."),
    "DEMO catalog (illustrative test data - connect the BonikPOS catalog for real use)",
    isDemoData: true);

/// <summary>A catalog held in memory (tests, or products already synced from the backend).</summary>
public sealed class InMemoryCatalogSource(IReadOnlyList<MedicineProduct> products, string description, bool isDemoData = false) : IMedicineCatalogSource
{
    public string Description { get; } = description;

    public bool IsDemoData { get; } = isDemoData;

    public Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new CatalogSnapshot(products, DateTimeOffset.UtcNow, Description, IsDemoData, []));
}

/// <summary>Generates a large SYNTHETIC catalog (pronounceable invented names) for performance tests and benchmarks only.</summary>
public static class SyntheticCatalog
{
    private static readonly string[] Onsets = ["b", "c", "d", "f", "g", "k", "l", "m", "n", "p", "r", "s", "t", "v", "z", "br", "cl", "pr", "tr", "st"];
    private static readonly string[] Vowels = ["a", "e", "i", "o", "u", "ai", "o", "a"];
    private static readonly string[] Codas = ["", "n", "x", "l", "s", "r", "m", "c"];
    private static readonly string[] Generics = ["Paracetamol", "Omeprazole", "Esomeprazole", "Cetirizine", "Fexofenadine", "Montelukast",
        "Azithromycin", "Ciprofloxacin", "Amoxicillin", "Metronidazole", "Metformin", "Losartan", "Amlodipine", "Pantoprazole", "Diclofenac"];
    private static readonly string[] Forms = ["Tablet", "Capsule", "Syrup", "Suspension", "Injection", "Drops", "Cream"];
    private static readonly string[] Strengths = ["5 mg", "10 mg", "20 mg", "40 mg", "100 mg", "250 mg", "500 mg", "120 mg/5 ml", "1 g"];

    public static IReadOnlyList<MedicineProduct> Generate(int count, int seed = 42)
    {
        var rng = new Random(seed);
        var products = new List<MedicineProduct>(count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (products.Count < count)
        {
            int syllables = rng.Next(2, 4);
            var name = string.Concat(Enumerable.Range(0, syllables).Select(_ => Onsets[rng.Next(Onsets.Length)] + Vowels[rng.Next(Vowels.Length)]))
                       + Codas[rng.Next(Codas.Length)];
            name = char.ToUpperInvariant(name[0]) + name[1..];
            if (!names.Add(name))
            {
                continue;
            }

            int variants = rng.Next(1, 4);
            for (int v = 0; v < variants && products.Count < count; v++)
            {
                products.Add(new MedicineProduct
                {
                    ProductId = $"SYN-{products.Count + 1:000000}",
                    BrandName = name,
                    GenericName = Generics[rng.Next(Generics.Length)],
                    Strength = Strengths[rng.Next(Strengths.Length)],
                    DosageForm = Forms[rng.Next(Forms.Length)],
                });
            }
        }

        return products;
    }
}
