using System.Text;
using BonikPOS.VoiceSearch.Core.Medicine;

namespace BonikPOS.VoiceSearch.MedicineSearch.Text;

/// <summary>Units, dosage forms, quantity words and filler words in English, Bangla and Banglish.</summary>
public static class MedicineVocabulary
{
    private static readonly Dictionary<string, StrengthUnit> SingleUnits = Build(new Dictionary<string, StrengthUnit>
    {
        ["mg"] = StrengthUnit.Milligram, ["mgs"] = StrengthUnit.Milligram, ["milligram"] = StrengthUnit.Milligram,
        ["milligrams"] = StrengthUnit.Milligram, ["milligramme"] = StrengthUnit.Milligram, ["মিলিগ্রাম"] = StrengthUnit.Milligram,
        ["মিগ্রা"] = StrengthUnit.Milligram, ["এমজি"] = StrengthUnit.Milligram,
        ["g"] = StrengthUnit.Gram, ["gm"] = StrengthUnit.Gram, ["gram"] = StrengthUnit.Gram, ["grams"] = StrengthUnit.Gram,
        ["গ্রাম"] = StrengthUnit.Gram,
        ["mcg"] = StrengthUnit.Microgram, ["µg"] = StrengthUnit.Microgram, ["ug"] = StrengthUnit.Microgram,
        ["microgram"] = StrengthUnit.Microgram, ["micrograms"] = StrengthUnit.Microgram, ["মাইক্রোগ্রাম"] = StrengthUnit.Microgram,
        ["ml"] = StrengthUnit.Millilitre, ["milliliter"] = StrengthUnit.Millilitre, ["millilitre"] = StrengthUnit.Millilitre,
        ["milliliters"] = StrengthUnit.Millilitre, ["মিলি"] = StrengthUnit.Millilitre, ["মিলিলিটার"] = StrengthUnit.Millilitre,
        ["এমএল"] = StrengthUnit.Millilitre,
        ["iu"] = StrengthUnit.InternationalUnit, ["unit"] = StrengthUnit.InternationalUnit, ["units"] = StrengthUnit.InternationalUnit,
        ["ইউনিট"] = StrengthUnit.InternationalUnit, ["আইইউ"] = StrengthUnit.InternationalUnit,
        ["%"] = StrengthUnit.Percent, ["percent"] = StrengthUnit.Percent, ["পার্সেন্ট"] = StrengthUnit.Percent,
        ["শতাংশ"] = StrengthUnit.Percent,
    });

    private static readonly Dictionary<(string, string), StrengthUnit> PairUnits = new()
    {
        [("m", "g")] = StrengthUnit.Milligram, [("এম", "জি")] = StrengthUnit.Milligram, [("milli", "gram")] = StrengthUnit.Milligram,
        [("মিলি", "গ্রাম")] = StrengthUnit.Milligram, [("m", "l")] = StrengthUnit.Millilitre, [("এম", "এল")] = StrengthUnit.Millilitre,
        [("micro", "gram")] = StrengthUnit.Microgram, [("i", "u")] = StrengthUnit.InternationalUnit,
    };

    private static readonly (DosageFormCategory Category, string[] Words)[] FormWords =
    [
        (DosageFormCategory.Tablet, ["tablet", "tablets", "tab", "tabs", "tablate", "ট্যাবলেট", "টেবলেট", "ট্যাবলেট", "ট্যাব", "টেবলেট"]),
        (DosageFormCategory.Capsule, ["capsule", "capsules", "cap", "caps", "ক্যাপসুল", "ক্যাপসুল", "ক্যাপ"]),
        (DosageFormCategory.Syrup, ["syrup", "syrups", "syp", "সিরাপ", "সিরাফ"]),
        (DosageFormCategory.Suspension, ["suspension", "susp", "সাসপেনশন", "সাসপেনসন"]),
        (DosageFormCategory.Drops, ["drop", "drops", "ড্রপ", "ড্রপস"]),
        (DosageFormCategory.Injection, ["injection", "inj", "injections", "ইনজেকশন", "ইঞ্জেকশন", "ইনজেকসন"]),
        (DosageFormCategory.Infusion, ["infusion", "saline", "স্যালাইন", "ইনফিউশন"]),
        (DosageFormCategory.Cream, ["cream", "ক্রিম"]),
        (DosageFormCategory.Ointment, ["ointment", "মলম", "অয়েন্টমেন্ট", "অয়েন্টমেন্ট", "অইন্টমেন্ট"]),
        (DosageFormCategory.Gel, ["gel", "জেল"]),
        (DosageFormCategory.Lotion, ["lotion", "লোশন"]),
        (DosageFormCategory.Inhaler, ["inhaler", "ইনহেলার"]),
        (DosageFormCategory.Powder, ["powder", "sachet", "পাউডার", "স্যাশে"]),
        (DosageFormCategory.Suppository, ["suppository", "suppositories", "সাপোজিটরি"]),
        (DosageFormCategory.Solution, ["solution", "সলিউশন"]),
        (DosageFormCategory.Spray, ["spray", "স্প্রে"]),
    ];

    private static readonly Dictionary<string, DosageFormCategory> FormLookup = BuildForms();

    private static readonly HashSet<string> QuantityWords = BuildSet(
        "strip", "strips", "pata", "pataa", "পাতা", "box", "boxes", "বক্স", "piece", "pieces", "pcs", "pc", "পিস", "টা", "টি",
        "খানা", "bottle", "bottles", "বোতল", "phile", "ফাইল", "vial", "vials", "ভায়াল", "ampoule", "pack", "packet", "প্যাকেট", "tube", "টিউব");

    private static readonly HashSet<string> StopWords = BuildSet(
        // English and Banglish fillers
        "please", "plz", "pls", "give", "me", "a", "an", "the", "of", "for", "need", "want", "have", "do", "you", "is", "are",
        "there", "any", "check", "find", "search", "show", "stock", "in", "and", "or", "some", "with", "my", "i", "we", "dao",
        "den", "din", "lagbe", "ache", "ase", "ki", "ar", "bhai", "apa", "to", "hobe", "ekta", "akta", "dekhen", "dekho",
        // Bangla
        "দিন", "দেন", "দাও", "দিবেন", "দেবেন", "আমাকে", "আমার", "লাগবে", "চাই", "আছে", "কি", "কী", "আর", "এবং", "ও", "খুঁজুন",
        "খুজুন", "দেখুন", "দেখো", "দেখেন", "স্টকে", "প্লিজ", "ভাই", "আপা", "তো", "হবে", "একটু", "একটা", "একটি", "জন্য",
        "দরকার", "রোগীর", "কোনটা", "কোন", "আছেন");

    public static bool TryUnit(IReadOnlyList<string> tokens, int index, out StrengthUnit unit, out int consumed)
    {
        consumed = 0;
        unit = default;
        if (index >= tokens.Count)
        {
            return false;
        }

        if (index + 1 < tokens.Count && PairUnits.TryGetValue((tokens[index], tokens[index + 1]), out unit))
        {
            consumed = 2;
            return true;
        }

        if (SingleUnits.TryGetValue(tokens[index], out unit))
        {
            consumed = 1;
            return true;
        }

        return false;
    }

    public static bool TryForm(string token, out DosageFormCategory form)
    {
        if (FormLookup.TryGetValue(token, out form))
        {
            return true;
        }

        // Spelling variants of the longer form words ("ট্যাবলিট", "capsul") through the phonetic key.
        if (token.Length >= 5)
        {
            string key = PhoneticKey.For(token);
            foreach (var (category, words) in FormWords)
            {
                foreach (var w in words)
                {
                    if (w.Length >= 5 && PhoneticSimilarity.Similarity(key, PhoneticKey.For(w)) >= 0.86)
                    {
                        form = category;
                        return true;
                    }
                }
            }
        }

        form = DosageFormCategory.Unknown;
        return false;
    }

    public static bool IsQuantityWord(string token) => QuantityWords.Contains(token);

    public static bool IsStopWord(string token) => StopWords.Contains(token);

    /// <summary>Category of a catalog dosage-form string ("Film Coated Tablet", "Powder for Suspension", "Eye Drops").</summary>
    public static DosageFormCategory ParseCatalogForm(string? dosageForm)
    {
        string text = TextNormalizer.Normalize(dosageForm);
        if (text.Length == 0)
        {
            return DosageFormCategory.Unknown;
        }

        // Most specific first: "powder for suspension" is dispensed as a suspension; "effervescent tablet" is a tablet.
        (string Needle, DosageFormCategory Category)[] rules =
        [
            ("suspension", DosageFormCategory.Suspension), ("syrup", DosageFormCategory.Syrup), ("drop", DosageFormCategory.Drops),
            ("infusion", DosageFormCategory.Infusion), ("injection", DosageFormCategory.Injection), ("inhaler", DosageFormCategory.Inhaler),
            ("capsule", DosageFormCategory.Capsule), ("tablet", DosageFormCategory.Tablet), ("cream", DosageFormCategory.Cream),
            ("ointment", DosageFormCategory.Ointment), ("gel", DosageFormCategory.Gel), ("lotion", DosageFormCategory.Lotion),
            ("suppositor", DosageFormCategory.Suppository), ("spray", DosageFormCategory.Spray), ("solution", DosageFormCategory.Solution),
            ("powder", DosageFormCategory.Powder), ("sachet", DosageFormCategory.Powder),
        ];
        foreach (var (needle, category) in rules)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return category;
            }
        }

        foreach (var token in TextNormalizer.Tokenize(text))
        {
            if (TryForm(token, out var f))
            {
                return f;
            }
        }

        return DosageFormCategory.Other;
    }

    /// <summary>Forms the owner may use interchangeably for the same product (e.g. "syrup" for an oral suspension).</summary>
    public static bool AreCompatible(DosageFormCategory a, DosageFormCategory b)
    {
        if (a == b)
        {
            return true;
        }

        static int Group(DosageFormCategory c) => c switch
        {
            DosageFormCategory.Syrup or DosageFormCategory.Suspension or DosageFormCategory.Solution => 1,
            DosageFormCategory.Cream or DosageFormCategory.Ointment or DosageFormCategory.Gel or DosageFormCategory.Lotion => 2,
            DosageFormCategory.Injection or DosageFormCategory.Infusion => 3,
            _ => 0,
        };

        int g = Group(a);
        return g != 0 && g == Group(b);
    }

    private static Dictionary<string, T> Build<T>(Dictionary<string, T> source)
    {
        var d = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (k, v) in source)
        {
            d[TextNormalizer.Normalize(k)] = v;
        }

        return d;
    }

    private static Dictionary<string, DosageFormCategory> BuildForms()
    {
        var d = new Dictionary<string, DosageFormCategory>(StringComparer.Ordinal);
        foreach (var (category, words) in FormWords)
        {
            foreach (var w in words)
            {
                d[TextNormalizer.Normalize(w)] = category;
            }
        }

        return d;
    }

    private static HashSet<string> BuildSet(params string[] words) =>
        new(words.Select(w => TextNormalizer.Normalize(w.Normalize(NormalizationForm.FormC))), StringComparer.Ordinal);
}
