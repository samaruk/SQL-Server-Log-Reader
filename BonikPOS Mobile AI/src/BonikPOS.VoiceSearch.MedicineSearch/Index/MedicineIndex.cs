using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.MedicineSearch.Text;

namespace BonikPOS.VoiceSearch.MedicineSearch.Index;

public enum NameVariantKind
{
    Brand,
    LocalName,
    Alias,
    Generic,
    /// <summary>One molecule of a combination product ("Caffeine" of "Paracetamol + Caffeine").</summary>
    GenericComponent,
}

/// <summary>One searchable name of a product, with its precomputed match keys.</summary>
public sealed record NameVariant(int ProductIndex, NameVariantKind Kind, string Text, string Compact, string Key, int TokenCount);

/// <summary>A catalog product plus the parsed attributes used for matching. The product itself is kept verbatim.</summary>
/// <param name="FormMentions">Every form the catalog form text mentions ("Powder for Suspension" -> Suspension, Powder).</param>
public sealed record IndexedProduct(
    int Index,
    MedicineProduct Product,
    string BrandCompact,
    IReadOnlyList<StrengthValue> Strengths,
    DosageFormCategory Form,
    IReadOnlyList<DosageFormCategory> FormMentions);

/// <summary>
/// Immutable search index over the catalog: exact maps on normalised text and on phonetic keys, and a trigram inverted
/// index on phonetic keys for approximate retrieval. Products are never merged - each product id stays separate even
/// when names sound alike; a repeated id from the source is reported as a warning and only its first row is indexed.
/// </summary>
public sealed class MedicineIndex
{
    private const int Alphabet = 40;
    private const int MinSkeletonLength = 3;
    private readonly Dictionary<string, List<int>> _byCompact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _bySkeleton = new(StringComparer.Ordinal);
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
            if (Skeleton(variant.Key) is { Length: >= MinSkeletonLength } skeleton)
            {
                Add(_bySkeleton, skeleton, v);
            }

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
                StrengthParser.Parse(product.Strength), MedicineVocabulary.ParseCatalogForm(product.DosageForm),
                MedicineVocabulary.CatalogFormMentions(product.DosageForm)));
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
                AddGenericVariants(variants, index, product.GenericName);
            }
        }

        return new MedicineIndex(indexed, variants, warnings);
    }

    public IndexedProduct? Find(string productId) => _byId.GetValueOrDefault(productId);

    public IReadOnlyList<int> ExactByCompact(string compact) => _byCompact.TryGetValue(compact, out var l) ? l : [];

    public IReadOnlyList<int> ExactByKey(string key) => _byKey.TryGetValue(key, out var l) ? l : [];

    /// <summary>
    /// Variants whose phonetic key has the same consonant skeleton. Retrieves spellings that differ only in vowels (for
    /// example Bangla spellings that omit or change vowels), which share few trigrams with the catalog spelling.
    /// </summary>
    public IReadOnlyList<int> ExactBySkeleton(string key) =>
        Skeleton(key) is { Length: >= MinSkeletonLength } s && _bySkeleton.TryGetValue(s, out var l) ? l : [];

    /// <summary>Consonant skeleton of a phonetic key: vowels removed.</summary>
    public static string Skeleton(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length);
        foreach (char c in key)
        {
            if (!PhoneticKey.IsVowel(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

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

    /// <summary>
    /// The full generic name, parenthesised synonyms on their own ("Vitamin C (Ascorbic Acid)" -> also "Vitamin C" and
    /// "Ascorbic Acid"), and each molecule of a combination as a <see cref="NameVariantKind.GenericComponent"/>.
    /// </summary>
    private static void AddGenericVariants(List<NameVariant> variants, int index, string generic)
    {
        AddVariant(variants, index, NameVariantKind.Generic, generic.Replace('+', ' '));
        int open = generic.IndexOf('(', StringComparison.Ordinal);
        int close = generic.IndexOf(')', StringComparison.Ordinal);
        if (open >= 0 && close > open)
        {
            string inside = generic[(open + 1)..close];
            string outside = generic[..open] + generic[(close + 1)..];
            AddVariant(variants, index, NameVariantKind.Generic, inside.Replace('+', ' '));
            AddVariant(variants, index, NameVariantKind.Generic, outside.Replace('+', ' '));
        }

        if (generic.Contains('+', StringComparison.Ordinal))
        {
            foreach (var part in generic.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length >= 4)
                {
                    AddVariant(variants, index, NameVariantKind.GenericComponent, part);
                }
            }
        }
    }

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
