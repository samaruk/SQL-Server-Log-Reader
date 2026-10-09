using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.MedicineSearch.Text;

namespace BonikPOS.VoiceSearch.MedicineSearch.Index;

public enum NameVariantKind
{
    Brand,
    LocalName,
    Alias,
    Generic,
}

/// <summary>One searchable name of a product, with its precomputed match keys.</summary>
public sealed record NameVariant(int ProductIndex, NameVariantKind Kind, string Text, string Compact, string Key, int TokenCount);

/// <summary>A catalog product plus the parsed attributes used for matching. The product itself is kept verbatim.</summary>
public sealed record IndexedProduct(
    int Index,
    MedicineProduct Product,
    string BrandCompact,
    IReadOnlyList<StrengthValue> Strengths,
    DosageFormCategory Form);

/// <summary>
/// Immutable search index over the catalog: exact maps on normalised text and on phonetic keys, and a trigram inverted
/// index on phonetic keys for approximate retrieval. Products are never merged - each product id stays separate even
/// when names sound alike; a repeated id from the source is reported as a warning and only its first row is indexed.
/// </summary>
public sealed class MedicineIndex
{
    private const int Alphabet = 40;
    private readonly Dictionary<string, List<int>> _byCompact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<int>> _trigrams = [];
    private readonly Dictionary<string, IndexedProduct> _byId = new(StringComparer.Ordinal);

    private MedicineIndex(List<IndexedProduct> products, List<NameVariant> variants, List<string> warnings)
    {
        Products = products;
        Variants = variants;
        Warnings = warnings;
        for (int v = 0; v < variants.Count; v++)
        {
            var variant = variants[v];
            Add(_byCompact, variant.Compact, v);
            Add(_byKey, variant.Key, v);
            foreach (int t in Trigrams(variant.Key))
            {
                if (!_trigrams.TryGetValue(t, out var list))
                {
                    _trigrams[t] = list = [];
                }

                if (list.Count == 0 || list[^1] != v)
                {
                    list.Add(v);
                }
            }
        }

        foreach (var p in products)
        {
            _byId[p.Product.ProductId] = p;
        }
    }

    public IReadOnlyList<IndexedProduct> Products { get; }

    public IReadOnlyList<NameVariant> Variants { get; }

    public IReadOnlyList<string> Warnings { get; }

    public static MedicineIndex Build(IReadOnlyList<MedicineProduct> products)
    {
        ArgumentNullException.ThrowIfNull(products);
        var indexed = new List<IndexedProduct>(products.Count);
        var variants = new List<NameVariant>(products.Count * 2);
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.ProductId) || string.IsNullOrWhiteSpace(product.BrandName))
            {
                warnings.Add($"Skipped a product without id or brand name ({product.ProductId}).");
                continue;
            }

            if (!seen.Add(product.ProductId))
            {
                warnings.Add($"Duplicate product id '{product.ProductId}' - only the first row is used.");
                continue;
            }

            int index = indexed.Count;
            string brand = TextNormalizer.Normalize(product.BrandName);
            indexed.Add(new IndexedProduct(
                index, product, TextNormalizer.Compact(brand),
                StrengthParser.Parse(product.Strength), MedicineVocabulary.ParseCatalogForm(product.DosageForm)));
            AddVariant(variants, index, NameVariantKind.Brand, product.BrandName);
            if (!string.IsNullOrWhiteSpace(product.LocalName))
            {
                AddVariant(variants, index, NameVariantKind.LocalName, product.LocalName);
            }

            foreach (var alias in product.Aliases)
            {
                AddVariant(variants, index, NameVariantKind.Alias, alias);
            }

            if (!string.IsNullOrWhiteSpace(product.GenericName))
            {
                AddVariant(variants, index, NameVariantKind.Generic, product.GenericName.Replace('+', ' '));
            }
        }

        return new MedicineIndex(indexed, variants, warnings);
    }

    public IndexedProduct? Find(string productId) => _byId.GetValueOrDefault(productId);

    public IReadOnlyList<int> ExactByCompact(string compact) => _byCompact.TryGetValue(compact, out var l) ? l : [];

    public IReadOnlyList<int> ExactByKey(string key) => _byKey.TryGetValue(key, out var l) ? l : [];

    /// <summary>Adds trigram hit counts of <paramref name="key"/> to <paramref name="counts"/> (indexed by variant).</summary>
    public void CountTrigramHits(string key, int[] counts, List<int> touched)
    {
        foreach (int t in Trigrams(key))
        {
            if (!_trigrams.TryGetValue(t, out var list))
            {
                continue;
            }

            foreach (int v in list)
            {
                if (counts[v]++ == 0)
                {
                    touched.Add(v);
                }
            }
        }
    }

    internal static IEnumerable<int> Trigrams(string key)
    {
        if (key.Length == 0)
        {
            yield break;
        }

        string padded = "^" + key + "$";
        for (int i = 0; i + 3 <= padded.Length; i++)
        {
            yield return (Code(padded[i]) * Alphabet + Code(padded[i + 1])) * Alphabet + Code(padded[i + 2]);
        }
    }

    private static int Code(char c) => c switch
    {
        >= 'a' and <= 'z' => c - 'a',
        >= '0' and <= '9' => 26 + (c - '0'),
        'C' => 36,
        '^' => 37,
        '$' => 38,
        _ => 39,
    };

    private static void AddVariant(List<NameVariant> variants, int index, NameVariantKind kind, string name)
    {
        string text = TextNormalizer.Normalize(name);
        if (text.Length == 0)
        {
            return;
        }

        variants.Add(new NameVariant(index, kind, text, TextNormalizer.Compact(text), PhoneticKey.For(text), TextNormalizer.Tokenize(text).Length));
    }

    private static void Add(Dictionary<string, List<int>> map, string key, int value)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }
}
