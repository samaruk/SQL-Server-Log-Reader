using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Evaluation.Search;

namespace BonikPOS.VoiceSearch.Evaluation.Synthetic;

/// <summary>
/// Generates labelled queries for a (synthetic) catalog by perturbing catalog names the way speech recognition and
/// typing do: number words, Bangla script spelling, Banglish respelling, typos, split words, generic-name queries, and
/// negatives (invented names that are not in the catalog and not within one edit of any catalog brand, strengths and
/// forms that a brand does not have, non-medicine phrases). Labels are computed by this generator from the catalog
/// values, independently of the matcher's own parsing code. The perturbation model is the developer's; results on this
/// set measure robustness to these perturbations, not real-world accuracy.
/// </summary>
public static partial class SyntheticCaseGenerator
{
    /// <summary>Phrase set B: used only for the held-out TEST split; never used while tuning thresholds.</summary>
    private static readonly string[] NotMedicineB =
    [
        "please hurry up", "my mother is sick", "it is very hot today", "do you sell mobile recharge", "where is the toilet",
        "can I pay later", "I will come back tonight", "give me a plastic bag", "your shop opens at nine", "this is too expensive",
        "আপনার দোকান কখন বন্ধ হয়", "আমি একটু পরে আসছি", "ব্যাগ দেন একটা", "খুচরা টাকা আছে", "বাসায় কেউ নেই",
        "ছেলেটা স্কুলে গেছে", "rasta te jam", "aaj onek gorom", "dokan kokhon bondho", "ektu tara tari koren",
        "amar mobile ta charge nai", "kalke abar ashbo", "bhaiya koto holo", "receipt ta den", "packet e diye den",
        "doctor er chamber kothay", "light chole gelo", "fan ta chalan", "good night", "see you later",
    ];

    /// <summary>
    /// Phrase set C: written for the FINAL held-out test split after sets A and B had been inspected; not used for any
    /// tuning or debugging.
    /// </summary>
    private static readonly string[] NotMedicineC =
    [
        "can you check the expiry date", "my son has an exam tomorrow", "the bus was late again", "put it in my account",
        "is the doctor sitting today", "how far is the hospital", "we need more chairs", "turn off the television",
        "did the delivery come", "write it on the slip", "পাশের দোকানে জিজ্ঞেস করেন", "রিকশা ভাড়া কত", "ফ্রিজটা নষ্ট হয়ে গেছে",
        "মেয়েটা কলেজে পড়ে", "আজকে হাট বার", "বিদ্যুৎ বিল দিতে হবে", "kono shomossha nai", "shondhay dekha hobe",
        "ma ke phone dao", "tomar nam ki", "school chuti kobe", "ekhane boshen", "matha byatha korche", "jhor hoyeche kal theke",
        "bazar theke ashlam", "network nai", "wifi password ki", "lunch korechen", "rain is coming", "thanks a lot brother",
    ];

    /// <summary>Phrase set A: used for the DEV split (thresholds were tuned on it).</summary>
    private static readonly string[] NotMedicine =
    [
        "hello", "good morning", "how much is this", "where is the receipt", "give me the change", "ki khobor", "dam koto",
        "ar kichu lagbe", "আজকে কত তারিখ", "দাম কত হয়েছে", "পরে আসবেন", "ধন্যবাদ", "thank you very much", "one minute please",
        "call the manager", "bhai ektu daran", "taka nai", "next customer", "close the door", "আচ্ছা ঠিক আছে", "ok fine",
        "no problem", "see you tomorrow", "where is my phone", "ঠান্ডা পানি দাও", "চা খাবেন", "bill koto holo", "card e dibo",
        "change nai", "kal ashben",
    ];

    private static readonly Dictionary<int, string> BanglaNumbers = new()
    {
        [1] = "এক", [2] = "দুই", [4] = "চার", [5] = "পাঁচ", [8] = "আট", [10] = "দশ", [15] = "পনেরো", [20] = "বিশ", [25] = "পঁচিশ",
        [30] = "ত্রিশ", [40] = "চল্লিশ", [50] = "পঞ্চাশ", [75] = "পঁচাত্তর", [80] = "আশি", [100] = "একশো", [120] = "একশো বিশ",
        [150] = "দেড়শো", [180] = "একশো আশি", [200] = "দুইশো", [250] = "আড়াইশো", [300] = "তিনশো", [400] = "চারশো",
        [500] = "পাঁচশো", [600] = "ছয়শো", [750] = "সাড়ে সাতশো", [850] = "আটশো পঞ্চাশ", [875] = "আটশো পঁচাত্তর",
    };

    private static readonly string[] Ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven",
        "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>Spoken form word -> (English, Bangla). Keys are the generator's own form classes.</summary>
    private static readonly Dictionary<string, (string En, string Bn)> FormWords = new(StringComparer.Ordinal)
    {
        ["tablet"] = ("tablet", "ট্যাবলেট"), ["capsule"] = ("capsule", "ক্যাপসুল"), ["syrup"] = ("syrup", "সিরাপ"),
        ["drops"] = ("drops", "ড্রপ"), ["injection"] = ("injection", "ইনজেকশন"), ["cream"] = ("cream", "ক্রিম"),
        ["ointment"] = ("ointment", "মলম"), ["gel"] = ("gel", "জেল"), ["inhaler"] = ("inhaler", "ইনহেলার"),
        ["powder"] = ("powder", "পাউডার"), ["saline"] = ("saline", "স্যালাইন"),
    };

    public static IReadOnlyList<SearchEvalCase> Generate(IReadOnlyList<MedicineProduct> products, int positives, int negatives, int seed, char phraseSet = 'A')
    {
        var phrases = phraseSet switch { 'B' => NotMedicineB, 'C' => NotMedicineC, _ => NotMedicine };
        var rng = new Random(seed);
        var byBrand = products.GroupBy(p => p.BrandName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var brands = byBrand.Keys.ToList();
        var compactBrands = new HashSet<string>(brands.Select(Compact), StringComparer.Ordinal);
        var soundBrands = brands.Select(b => SoundsLike(Compact(b))).Distinct(StringComparer.Ordinal).ToList();
        var byGeneric = products.Where(p => p.GenericName is { } g && !g.Contains('+') && !g.Contains('('))
            .GroupBy(p => p.GenericName!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var generics = byGeneric.Keys.ToList();
        var cases = new List<SearchEvalCase>();
        int attempts = 0;
        while (cases.Count < positives && attempts++ < positives * 20)
        {
            var p = products[rng.Next(products.Count)];
            var skus = byBrand[p.BrandName];
            double t = rng.NextDouble();
            SearchEvalCase? c = t switch
            {
                < 0.22 => BrandStrength(p, skus, rng, "brand-strength", p.BrandName, english: true),
                < 0.30 => BrandOnly(p, skus),
                < 0.40 => BrandForm(p, skus, rng),
                < 0.52 => Typo(p, skus, rng, compactBrands),
                < 0.64 => BrandStrength(p, skus, rng, "banglish-respelling", Respell(p.BrandName, rng), english: true),
                < 0.78 => BanglaScript(p, skus, rng),
                < 0.88 => BrandStrength(p, skus, rng, "asr-split", Split(p.BrandName, rng), english: true, words: true),
                _ => GenericStrength(generics, byGeneric, rng),
            };
            if (c is not null && !cases.Any(x => x.Query == c.Query))
            {
                cases.Add(c with { Id = $"syn-{cases.Count + 1:00000}" });
            }
        }

        int negStart = cases.Count;
        attempts = 0;
        while (cases.Count - negStart < negatives && attempts++ < negatives * 50)
        {
            double t = rng.NextDouble();
            SearchEvalCase? c = t switch
            {
                < 0.40 => HeldOutName(rng, compactBrands, soundBrands),
                < 0.65 => MissingStrength(products[rng.Next(products.Count)], byBrand, rng),
                < 0.82 => MissingForm(products[rng.Next(products.Count)], byBrand, rng),
                _ => Case(phrases[rng.Next(phrases.Length)], [], "neg-not-medicine", "mixed"),
            };
            if (c is not null && !cases.Any(x => x.Query == c.Query))
            {
                cases.Add(c with { Id = $"syn-{cases.Count + 1:00000}" });
            }
        }

        return cases;
    }

    public static void Write(string path, IReadOnlyList<SearchEvalCase> cases)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        File.WriteAllLines(path, cases.Select(c => JsonSerializer.Serialize(c, options)));
    }

    // ---- positive templates -------------------------------------------------------------------------------------

    private static SearchEvalCase? BrandStrength(MedicineProduct p, List<MedicineProduct> skus, Random rng, string kind, string spokenName, bool english, bool words = false)
    {
        var amounts = Amounts(p.Strength);
        if (amounts.Count == 0)
        {
            return null;
        }

        decimal amount = amounts[0];
        var expected = skus.Where(s => Amounts(s.Strength).Contains(amount)).Select(s => s.ProductId).ToList();
        string number = words || rng.NextDouble() < 0.3 ? EnglishWords(amount) ?? Num(amount) : Num(amount);
        string unit = UnitWord(p.Strength, rng);
        string query = $"{spokenName} {number}{(unit.Length > 0 && rng.NextDouble() < 0.5 ? " " + unit : string.Empty)}";
        return Case(query, expected, kind, english ? "en" : "banglish");
    }

    private static SearchEvalCase BrandOnly(MedicineProduct p, List<MedicineProduct> skus)
    {
        var c = Case(p.BrandName, [.. skus.Select(s => s.ProductId)], "brand-only", "en");
        return skus.Count > 1 ? c with { Expect = c.Expect with { Decisions = ["Ambiguous"] } } : c;
    }

    private static SearchEvalCase? BrandForm(MedicineProduct p, List<MedicineProduct> skus, Random rng)
    {
        string? form = FormClass(p.DosageForm);
        if (form is null)
        {
            return null;
        }

        var expected = skus.Where(s => FormClass(s.DosageForm) == form).Select(s => s.ProductId).ToList();
        return Case($"{p.BrandName} {FormWords[form].En}", expected, "brand-form", "en");
    }

    private static SearchEvalCase? Typo(MedicineProduct p, List<MedicineProduct> skus, Random rng, HashSet<string> compactBrands)
    {
        string first = p.BrandName.Split(' ')[0];
        if (first.Length < 5)
        {
            return null; // one edit in a short name is too destructive to label as the same product
        }

        string typo = Edit(first.ToLowerInvariant(), rng);
        string full = typo + p.BrandName[first.Length..];
        // Unlabelable if the typo is another brand or within one edit of another brand.
        if (compactBrands.Contains(Compact(full)) || compactBrands.Any(b => b != Compact(p.BrandName) && Math.Abs(b.Length - Compact(full).Length) <= 1 && Levenshtein(b, Compact(full)) <= 1))
        {
            return null;
        }

        var c = BrandStrength(p, skus, rng, "typo", full, english: true);
        return c is null ? null : c with { Source = "Typed" };
    }

    private static SearchEvalCase? BanglaScript(MedicineProduct p, List<MedicineProduct> skus, Random rng)
    {
        var amounts = Amounts(p.Strength);
        string name = string.Join(' ', p.BrandName.Split(' ').Select(w => ToBangla(w, rng)));
        if (amounts.Count == 0)
        {
            return null;
        }

        decimal amount = amounts[0];
        var expected = skus.Where(s => Amounts(s.Strength).Contains(amount)).Select(s => s.ProductId).ToList();
        string number = rng.NextDouble() < 0.5 && amount == decimal.Truncate(amount) && BanglaNumbers.TryGetValue((int)amount, out var w) ? w : BanglaDigits(Num(amount));
        string query = $"{name} {number}";
        if (rng.NextDouble() < 0.3 && FormClass(p.DosageForm) is { } f)
        {
            query += " " + FormWords[f].Bn;
            expected = [.. skus.Where(s => Amounts(s.Strength).Contains(amount) && FormClass(s.DosageForm) == f).Select(s => s.ProductId)];
        }

        return Case(query, expected, "bangla-script", "bn");
    }

    private static SearchEvalCase? GenericStrength(List<string> generics, Dictionary<string, List<MedicineProduct>> byGeneric, Random rng)
    {
        string g = generics[rng.Next(generics.Count)];
        var items = byGeneric[g];
        var p = items[rng.Next(items.Count)];
        var amounts = Amounts(p.Strength);
        if (amounts.Count == 0)
        {
            return null;
        }

        var expected = items.Where(s => Amounts(s.Strength).Contains(amounts[0])).Select(s => s.ProductId).ToList();
        return Case($"{g.ToLowerInvariant()} {Num(amounts[0])}", expected, "generic-strength", "en");
    }

    // ---- negative templates -------------------------------------------------------------------------------------

    private static SearchEvalCase? HeldOutName(Random rng, HashSet<string> compactBrands, List<string> soundBrands)
    {
        string name = RealisticSyntheticCatalog.NewName(rng);
        string compact = Compact(name);
        string sound = SoundsLike(compact);
        // A spoken negative must be distinguishable by ear: at least two edits away from every catalog brand both in
        // spelling and in a simple sound-alike spelling (ph=f, c=k/s, doubled letters, ee=i, ...).
        if (compactBrands.Contains(compact) || compactBrands.Any(b => Math.Abs(b.Length - compact.Length) <= 1 && Levenshtein(b, compact) <= 1)
            || soundBrands.Any(b => Math.Abs(b.Length - sound.Length) <= 1 && Levenshtein(b, sound) <= 1))
        {
            return null; // too close to a catalog brand to be an unambiguous negative
        }

        string query = rng.NextDouble() < 0.6 ? $"{name} {(rng.Next(4) switch { 0 => "500", 1 => "20", 2 => "10 mg", _ => "250" })}" : name;
        return Case(query, [], "neg-unknown-brand", "en");
    }

    private static SearchEvalCase? MissingStrength(MedicineProduct p, Dictionary<string, List<MedicineProduct>> byBrand, Random rng)
    {
        // Every amount the brand has, with gram strengths also as milligrams ("1 g" = 1000), so that an equivalent
        // amount is never labelled as missing.
        var have = byBrand[p.BrandName].SelectMany(s => AllAmounts(s.Strength)).ToHashSet();
        decimal[] common = [5, 10, 20, 40, 50, 100, 150, 200, 250, 400, 500, 750, 1000];
        var missing = common.Where(a => !have.Contains(a)).ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        return Case($"{p.BrandName} {Num(missing[rng.Next(missing.Count)])}", [], "neg-strength-mismatch", "en");
    }

    private static SearchEvalCase? MissingForm(MedicineProduct p, Dictionary<string, List<MedicineProduct>> byBrand, Random rng)
    {
        var have = byBrand[p.BrandName].Select(s => FormClass(s.DosageForm)).ToHashSet();
        // Forms the owner might use interchangeably are treated as present (syrup ~ suspension/powder for suspension).
        string[] candidates = ["tablet", "capsule", "syrup", "injection", "cream", "drops"];
        var missing = candidates.Where(f => !have.Contains(f) && !(f == "syrup" && have.Contains("powder"))).ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        return Case($"{p.BrandName} {FormWords[missing[rng.Next(missing.Count)]].En}", [], "neg-form-mismatch", "en");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static SearchEvalCase Case(string query, List<string> products, string kind, string lang) => new()
    {
        Id = string.Empty,
        Query = query,
        Kind = [kind],
        Lang = lang,
        Expect = new SearchExpectation { Products = products },
    };

    /// <summary>The generator's own form classes (independent of the matcher's vocabulary).</summary>
    internal static string? FormClass(string? dosageForm)
    {
        string f = (dosageForm ?? string.Empty).ToLowerInvariant();
        return f switch
        {
            _ when f.Contains("suspension") || f.Contains("syrup") => "syrup",
            _ when f.Contains("drop") => "drops",
            _ when f.Contains("inhaler") => "inhaler",
            _ when f.Contains("infusion") => "saline",
            _ when f.Contains("injection") => "injection",
            _ when f.Contains("capsule") => "capsule",
            _ when f.Contains("tablet") => "tablet",
            _ when f.Contains("ointment") => "ointment",
            _ when f.Contains("cream") => "cream",
            _ when f.Contains("gel") => "gel",
            _ when f.Contains("powder") || f.Contains("sachet") => "powder",
            _ => null,
        };
    }

    /// <summary>
    /// Numeric strength amounts as written in the catalog. Gram strengths return an empty list: a bare spoken "1" for
    /// "1 g" is not a fair query (people say "one gram" or "1000"), so those SKUs are not used for strength queries.
    /// </summary>
    internal static List<decimal> Amounts(string? strength)
    {
        var list = new List<decimal>();
        foreach (Match m in AmountRegex().Matches(strength ?? string.Empty))
        {
            if (m.Groups[2].Value == "g")
            {
                return [];
            }

            list.Add(decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        return list;
    }

    private static IEnumerable<decimal> AllAmounts(string? strength)
    {
        foreach (Match m in AmountRegex().Matches(strength ?? string.Empty))
        {
            decimal v = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            yield return v;
            if (m.Groups[2].Value == "g")
            {
                yield return v * 1000;
            }
        }
    }

    private static string UnitWord(string? strength, Random rng)
    {
        string s = strength ?? string.Empty;
        if (s.Contains("mcg", StringComparison.Ordinal))
        {
            return "mcg";
        }

        if (Regex.IsMatch(s, @"\d\s*mg"))
        {
            return rng.NextDouble() < 0.5 ? "mg" : "milligram";
        }

        return string.Empty;
    }

    private static string Num(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string? EnglishWords(decimal v)
    {
        if (v != decimal.Truncate(v) || v <= 0 || v > 999)
        {
            return null;
        }

        int n = (int)v;
        var parts = new List<string>();
        if (n >= 100)
        {
            parts.Add(Ones[n / 100]);
            parts.Add("hundred");
            n %= 100;
        }

        if (n >= 20)
        {
            parts.Add(Tens[n / 10]);
            if (n % 10 != 0)
            {
                parts.Add(Ones[n % 10]);
            }
        }
        else if (n > 0)
        {
            parts.Add(Ones[n]);
        }

        return string.Join(' ', parts);
    }

    private static string BanglaDigits(string ascii)
    {
        var sb = new StringBuilder(ascii.Length);
        foreach (char c in ascii)
        {
            sb.Append(c is >= '0' and <= '9' ? (char)('০' + (c - '0')) : c);
        }

        return sb.ToString();
    }

    /// <summary>Banglish-style respelling: one or two sound-preserving spelling changes.</summary>
    internal static string Respell(string brand, Random rng)
    {
        string s = brand.ToLowerInvariant();
        (string From, string To)[] rules =
        [
            ("ph", "f"), ("c", "k"), ("ck", "k"), ("x", "ks"), ("z", "j"), ("v", "bh"), ("ee", "i"), ("ou", "u"), ("y", "i"),
            ("th", "t"), ("sh", "s"), ("ai", "e"), ("qu", "k"), ("ce", "se"), ("ci", "si"),
        ];
        int changes = 0;
        foreach (var (from, to) in rules.OrderBy(_ => rng.Next()))
        {
            if (changes >= 2)
            {
                break;
            }

            int i = s.IndexOf(from, StringComparison.Ordinal);
            if (i >= 0)
            {
                s = s[..i] + to + s[(i + from.Length)..];
                changes++;
            }
        }

        if (changes == 0)
        {
            // Double a consonant ("napa" -> "nappa") as Banglish writers often do.
            int i = s.IndexOfAny("bdgklmnprst".ToCharArray(), 1);
            if (i < 0)
            {
                return s;
            }

            s = s.Insert(i, s[i].ToString());
        }

        return s;
    }

    /// <summary>Splits a brand into two words at a vowel/consonant boundary, as an English recogniser often does.</summary>
    internal static string Split(string brand, Random rng)
    {
        string first = brand.Split(' ')[0].ToLowerInvariant();
        var cuts = Enumerable.Range(2, Math.Max(0, first.Length - 3)).Where(i => IsVowel(first[i - 1]) && !IsVowel(first[i])).ToList();
        if (cuts.Count == 0)
        {
            return brand.ToLowerInvariant();
        }

        int cut = cuts[rng.Next(cuts.Count)];
        return first[..cut] + " " + first[cut..] + brand[first.Length..].ToLowerInvariant();
    }

    private static string Edit(string s, Random rng)
    {
        const string letters = "abcdefgiklmnoprstuvz";
        int i = rng.Next(1, s.Length);
        return rng.Next(4) switch
        {
            0 => s[..i] + letters[rng.Next(letters.Length)] + s[(i + 1)..],
            1 => s[..i] + letters[rng.Next(letters.Length)] + s[i..],
            2 => s[..i] + s[(i + 1)..],
            _ => i + 1 < s.Length ? s[..i] + s[i + 1] + s[i] + s[(i + 2)..] : s[..i] + s[(i + 1)..],
        };
    }

    /// <summary>Spells an invented Latin brand name in Bengali script the way a Bangla writer might (rule based).</summary>
    internal static string ToBangla(string word, Random rng)
    {
        string s = word.ToLowerInvariant();
        var sb = new StringBuilder();
        int i = 0;
        bool afterConsonant = false;
        while (i < s.Length)
        {
            string two = i + 1 < s.Length ? s.Substring(i, 2) : string.Empty;
            string? vowel = two switch { "ai" => "ai", "ee" => "ee", "ou" => "ou", "oo" => "oo", _ => null } ?? (IsVowel(s[i]) ? s[i].ToString() : null);
            if (vowel is not null)
            {
                sb.Append(afterConsonant ? VowelSign(vowel) : IndependentVowel(vowel));
                i += vowel.Length;
                afterConsonant = false;
                continue;
            }

            string? digraph = two is "ph" or "ch" or "sh" or "th" or "kh" or "gh" ? two : null;
            string consonant = digraph ?? s[i].ToString();
            char next = i + consonant.Length < s.Length ? s[i + consonant.Length] : '\0';
            string letter = consonant switch
            {
                "ph" => "ফ", "ch" => "চ", "sh" => "শ", "th" => "থ", "kh" => "খ", "gh" => "ঘ",
                "b" => "ব", "c" => next is 'e' or 'i' or 'y' ? "স" : "ক", "d" => "ড", "f" => "ফ", "g" => next is 'e' or 'i' ? "জ" : "গ",
                "h" => "হ", "j" => "জ", "k" => "ক", "l" => "ল", "m" => "ম", "n" => "ন", "p" => "প", "q" => "ক", "r" => "র",
                "s" => "স", "t" => "ট", "v" => "ভ", "w" => "ও", "x" => "ক্স", "y" => "য়", "z" => "জ",
                _ => string.Empty,
            };
            if (afterConsonant && letter.Length > 0 && rng.NextDouble() < 0.5)
            {
                sb.Append('্'); // write the cluster as a conjunct half of the time ("ক্ল" vs "কল")
            }

            sb.Append(letter);
            i += consonant.Length;
            afterConsonant = letter.Length > 0;
        }

        return sb.ToString();

        static string VowelSign(string v) => v switch { "a" => "া", "e" => "ে", "i" => "ি", "o" => "ো", "u" => "ু", "ai" => "াই", "ee" => "ী", "ou" => "ৌ", "oo" => "ু", _ => string.Empty };
        static string IndependentVowel(string v) => v switch { "a" => "আ", "e" => "এ", "i" => "ই", "o" => "ও", "u" => "উ", "ai" => "আই", "ee" => "ঈ", "ou" => "ঔ", "oo" => "উ", _ => string.Empty };
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    /// <summary>The generator's own sound-alike spelling (independent of the matcher's phonetic key).</summary>
    internal static string SoundsLike(string compact)
    {
        string s = compact;
        foreach (var (from, to) in new[] { ("ph", "f"), ("ck", "k"), ("qu", "k"), ("sh", "s"), ("th", "t"), ("kh", "k"), ("gh", "g"),
                     ("bh", "b"), ("dh", "d"), ("ee", "i"), ("oo", "u"), ("ou", "u"), ("ai", "e"), ("ce", "se"), ("ci", "si"), ("cy", "si") })
        {
            s = s.Replace(from, to, StringComparison.Ordinal);
        }

        var sb = new StringBuilder(s.Length);
        foreach (char c0 in s)
        {
            char c = c0 switch { 'c' or 'q' => 'k', 'z' => 'j', 'v' => 'b', 'w' => 'o', 'y' => 'i', _ => c0 };
            if (c == 'x')
            {
                sb.Append("ks");
                continue;
            }

            if (sb.Length > 0 && sb[^1] == c)
            {
                continue; // doubled letters sound single
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Compact(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(mg|mcg|g|ml|iu|IU|%)?", RegexOptions.CultureInvariant)]
    private static partial Regex AmountRegex();
}
