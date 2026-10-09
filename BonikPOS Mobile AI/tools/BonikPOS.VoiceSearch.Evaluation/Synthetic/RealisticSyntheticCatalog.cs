using System.Text.Encodings.Web;
using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Medicine;

namespace BonikPOS.VoiceSearch.Evaluation.Synthetic;

/// <summary>
/// Generates a SYNTHETIC pharmacy catalog with the structure of a real one: each invented brand belongs to one molecule
/// (or a fixed combination), has one to four SKUs with strengths and forms that exist for that molecule, some brands
/// have line extensions ("Plus", "Extra", "XR"), and about 3% of brands are deliberately one letter away from another
/// brand (distinct products with confusable names). No Bangla local names are generated, as in a typical POS export.
/// Generic names and strengths are common public INN/strength combinations; brand names are invented and ids are
/// "SYN-nnnnnn". The data is for evaluating and benchmarking the matcher only.
/// </summary>
public static class RealisticSyntheticCatalog
{
    /// <summary>Molecule -> SKU options (strength, form).</summary>
    internal static readonly (string Generic, (string Strength, string Form)[] Skus)[] Molecules =
    [
        ("Paracetamol", [("500 mg", "Tablet"), ("120 mg/5 ml", "Suspension"), ("80 mg/ml", "Pediatric Drops"), ("665 mg", "Extended Release Tablet"), ("1 g", "Tablet")]),
        ("Paracetamol + Caffeine", [("500 mg + 65 mg", "Tablet")]),
        ("Omeprazole", [("20 mg", "Capsule"), ("40 mg", "Capsule"), ("40 mg", "Injection")]),
        ("Esomeprazole", [("20 mg", "Tablet"), ("40 mg", "Tablet"), ("20 mg", "Capsule"), ("40 mg", "Injection")]),
        ("Pantoprazole", [("20 mg", "Tablet"), ("40 mg", "Tablet"), ("40 mg", "Injection")]),
        ("Rabeprazole", [("20 mg", "Tablet")]),
        ("Ranitidine", [("150 mg", "Tablet"), ("75 mg/5 ml", "Syrup")]),
        ("Famotidine", [("20 mg", "Tablet"), ("40 mg", "Tablet")]),
        ("Domperidone", [("10 mg", "Tablet"), ("5 mg/5 ml", "Suspension")]),
        ("Ondansetron", [("8 mg", "Tablet"), ("4 mg", "Tablet"), ("8 mg/4 ml", "Injection")]),
        ("Cetirizine", [("10 mg", "Tablet"), ("5 mg/5 ml", "Syrup")]),
        ("Levocetirizine", [("5 mg", "Tablet"), ("2.5 mg/5 ml", "Syrup")]),
        ("Fexofenadine", [("120 mg", "Tablet"), ("180 mg", "Tablet"), ("30 mg/5 ml", "Suspension")]),
        ("Loratadine", [("10 mg", "Tablet"), ("5 mg/5 ml", "Syrup")]),
        ("Desloratadine", [("5 mg", "Tablet"), ("2.5 mg/5 ml", "Syrup")]),
        ("Montelukast", [("10 mg", "Tablet"), ("5 mg", "Chewable Tablet"), ("4 mg", "Chewable Tablet")]),
        ("Salbutamol", [("4 mg", "Tablet"), ("2 mg/5 ml", "Syrup"), ("100 mcg/puff", "Inhaler")]),
        ("Ambroxol", [("15 mg/5 ml", "Syrup"), ("30 mg", "Tablet")]),
        ("Azithromycin", [("500 mg", "Tablet"), ("250 mg", "Capsule"), ("200 mg/5 ml", "Powder for Suspension")]),
        ("Clarithromycin", [("500 mg", "Tablet"), ("250 mg", "Tablet")]),
        ("Amoxicillin", [("500 mg", "Capsule"), ("250 mg", "Capsule"), ("125 mg/5 ml", "Powder for Suspension")]),
        ("Amoxicillin + Clavulanic Acid", [("500 mg + 125 mg", "Tablet"), ("875 mg + 125 mg", "Tablet")]),
        ("Cefixime", [("200 mg", "Capsule"), ("400 mg", "Capsule"), ("100 mg/5 ml", "Powder for Suspension")]),
        ("Cefuroxime", [("250 mg", "Tablet"), ("500 mg", "Tablet"), ("750 mg", "Injection")]),
        ("Cephradine", [("500 mg", "Capsule"), ("250 mg", "Capsule")]),
        ("Ciprofloxacin", [("500 mg", "Tablet"), ("250 mg", "Tablet"), ("0.3%", "Eye Drops")]),
        ("Levofloxacin", [("500 mg", "Tablet"), ("750 mg", "Tablet")]),
        ("Doxycycline", [("100 mg", "Capsule")]),
        ("Flucloxacillin", [("500 mg", "Capsule"), ("250 mg", "Capsule")]),
        ("Metronidazole", [("400 mg", "Tablet"), ("200 mg/5 ml", "Suspension"), ("500 mg/100 ml", "Infusion")]),
        ("Fluconazole", [("150 mg", "Capsule"), ("50 mg", "Capsule")]),
        ("Albendazole", [("400 mg", "Tablet"), ("200 mg/5 ml", "Suspension")]),
        ("Diclofenac Sodium", [("50 mg", "Tablet"), ("100 mg", "Sustained Release Tablet"), ("1%", "Gel")]),
        ("Naproxen", [("250 mg", "Tablet"), ("500 mg", "Tablet")]),
        ("Ibuprofen", [("400 mg", "Tablet"), ("100 mg/5 ml", "Suspension")]),
        ("Ketorolac Tromethamine", [("10 mg", "Tablet"), ("30 mg/ml", "Injection")]),
        ("Tolfenamic Acid", [("200 mg", "Tablet")]),
        ("Aceclofenac", [("100 mg", "Tablet")]),
        ("Metformin Hydrochloride", [("500 mg", "Tablet"), ("850 mg", "Tablet"), ("1 g", "Extended Release Tablet")]),
        ("Gliclazide", [("80 mg", "Tablet"), ("30 mg", "Modified Release Tablet")]),
        ("Glimepiride", [("1 mg", "Tablet"), ("2 mg", "Tablet")]),
        ("Sitagliptin", [("50 mg", "Tablet"), ("100 mg", "Tablet")]),
        ("Losartan Potassium", [("50 mg", "Tablet"), ("25 mg", "Tablet")]),
        ("Amlodipine", [("5 mg", "Tablet"), ("10 mg", "Tablet")]),
        ("Bisoprolol Fumarate", [("2.5 mg", "Tablet"), ("5 mg", "Tablet")]),
        ("Atorvastatin", [("10 mg", "Tablet"), ("20 mg", "Tablet"), ("40 mg", "Tablet")]),
        ("Rosuvastatin", [("5 mg", "Tablet"), ("10 mg", "Tablet"), ("20 mg", "Tablet")]),
        ("Clopidogrel", [("75 mg", "Tablet")]),
        ("Aspirin", [("75 mg", "Tablet"), ("300 mg", "Tablet")]),
        ("Furosemide", [("40 mg", "Tablet"), ("20 mg/2 ml", "Injection")]),
        ("Atenolol", [("50 mg", "Tablet"), ("25 mg", "Tablet")]),
        ("Calcium Carbonate + Vitamin D3", [("500 mg + 200 IU", "Tablet"), ("600 mg + 400 IU", "Tablet")]),
        ("Vitamin C (Ascorbic Acid)", [("250 mg", "Chewable Tablet"), ("500 mg", "Tablet")]),
        ("Vitamin B1 + B6 + B12", [("100 mg + 200 mg + 200 mcg", "Tablet")]),
        ("Ferrous Fumarate + Folic Acid", [("200 mg + 0.2 mg", "Capsule")]),
        ("Zinc Sulphate", [("20 mg", "Tablet"), ("10 mg/5 ml", "Syrup")]),
        ("Oral Rehydration Salts", [("10.25 g", "Powder (Sachet)")]),
        ("Prednisolone", [("5 mg", "Tablet"), ("10 mg", "Tablet")]),
        ("Betamethasone", [("0.1%", "Cream"), ("0.5 mg", "Tablet")]),
        ("Clotrimazole", [("1%", "Cream"), ("100 mg", "Vaginal Tablet")]),
        ("Mupirocin", [("2%", "Ointment")]),
        ("Fusidic Acid", [("2%", "Cream")]),
        ("Silver Sulfadiazine", [("1%", "Cream")]),
        ("Neomycin + Bacitracin", [("0.5% + 500 IU", "Powder"), ("0.5% + 500 IU", "Ointment")]),
        ("Hyoscine Butylbromide", [("10 mg", "Tablet"), ("20 mg/ml", "Injection")]),
        ("Drotaverine", [("40 mg", "Tablet"), ("80 mg", "Tablet")]),
        ("Alprazolam", [("0.25 mg", "Tablet"), ("0.5 mg", "Tablet")]),
        ("Clonazepam", [("0.5 mg", "Tablet"), ("2 mg", "Tablet")]),
        ("Sertraline", [("50 mg", "Tablet")]),
        ("Thyroxine Sodium", [("50 mcg", "Tablet"), ("100 mcg", "Tablet")]),
        ("Tamsulosin", [("0.4 mg", "Modified Release Capsule")]),
        ("Lactulose", [("3.35 g/5 ml", "Syrup")]),
        ("Antacid (Aluminium + Magnesium Hydroxide)", [("200 ml", "Suspension"), ("400 mg", "Chewable Tablet")]),
        ("Sodium Chloride", [("0.9%", "Infusion")]),
        ("Dextrose", [("5%", "Infusion")]),
        ("Insulin (Human)", [("100 IU/ml", "Injection")]),
    ];

    private static readonly string[] Onsets = ["b", "c", "d", "f", "g", "h", "j", "k", "l", "m", "n", "p", "r", "s", "t", "v", "z",
        "br", "cl", "pr", "tr", "st", "fl", "gl", "pl", "sl", "sp", "ph", "ch", "sh"];
    private static readonly string[] Vowels = ["a", "e", "i", "o", "u", "a", "o", "i", "ai", "ee", "ou"];
    private static readonly string[] Endings = ["", "", "", "n", "x", "l", "s", "r", "m", "c", "t", "d", "k", "ne", "na", "ra", "ta", "ol",
        "il", "an", "ex", "in", "on", "ax", "um", "id", "ac", "en", "or", "ix"];
    private static readonly string[] Extensions = ["Plus", "Extra", "DS", "XR", "Forte", "Max", "Kid", "SR", "MR", "Duo"];

    public static IReadOnlyList<MedicineProduct> Generate(int count, int seed)
    {
        var rng = new Random(seed);
        var products = new List<MedicineProduct>(count);
        var brands = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (products.Count < count)
        {
            string brand;
            if (brands.Count > 50 && rng.NextDouble() < 0.03)
            {
                // Confusable but distinct brand: one letter changed/inserted/deleted from an existing brand.
                brand = Mutate(brands[rng.Next(brands.Count)], rng);
            }
            else
            {
                brand = NewName(rng);
            }

            if (brand.Length < 3 || !used.Add(brand))
            {
                continue;
            }

            brands.Add(brand);
            var (generic, skus) = Molecules[rng.Next(Molecules.Length)];
            var chosen = skus.OrderBy(_ => rng.Next()).Take(rng.Next(1, Math.Min(4, skus.Length) + 1)).ToList();
            foreach (var (strength, form) in chosen)
            {
                Add(products, brand, generic, strength, form, count);
            }

            if (rng.NextDouble() < 0.12 && products.Count < count)
            {
                // Line extension: "<Brand> Plus" etc. with one SKU of the same or a related molecule.
                string ext = brand + " " + Extensions[rng.Next(Extensions.Length)];
                if (used.Add(ext))
                {
                    var (strength, form) = skus[rng.Next(skus.Length)];
                    Add(products, ext, generic, strength, form, count);
                }
            }
        }

        return products;
    }

    public static void Write(string path, IReadOnlyList<MedicineProduct> products, int seed)
    {
        var doc = new
        {
            notice = $"SYNTHETIC CATALOG generated by tools/BonikPOS.VoiceSearch.Evaluation (seed {seed}). Invented brand names; " +
                     "for evaluating and benchmarking the matcher only - not a drug database.",
            products = products.Select(p => new { productId = p.ProductId, brandName = p.BrandName, genericName = p.GenericName, strength = p.Strength, dosageForm = p.DosageForm }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    internal static string NewName(Random rng)
    {
        int syllables = rng.NextDouble() < 0.7 ? 2 : 3;
        var name = string.Concat(Enumerable.Range(0, syllables).Select(_ => Onsets[rng.Next(Onsets.Length)] + Vowels[rng.Next(Vowels.Length)]))
                   + Endings[rng.Next(Endings.Length)];
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string Mutate(string name, Random rng)
    {
        var chars = name.ToLowerInvariant().ToCharArray().ToList();
        int pos = rng.Next(1, chars.Count);
        const string letters = "abcdefgiklmnoprstuvxz";
        switch (rng.Next(3))
        {
            case 0:
                chars[pos] = letters[rng.Next(letters.Length)];
                break;
            case 1:
                chars.Insert(pos, letters[rng.Next(letters.Length)]);
                break;
            default:
                if (chars.Count > 4)
                {
                    chars.RemoveAt(pos);
                }

                break;
        }

        var s = new string([.. chars]);
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static void Add(List<MedicineProduct> products, string brand, string generic, string strength, string form, int count)
    {
        if (products.Count >= count)
        {
            return;
        }

        products.Add(new MedicineProduct
        {
            ProductId = $"SYN-{products.Count + 1:000000}",
            BrandName = brand,
            GenericName = generic,
            Strength = strength,
            DosageForm = form,
        });
    }
}
