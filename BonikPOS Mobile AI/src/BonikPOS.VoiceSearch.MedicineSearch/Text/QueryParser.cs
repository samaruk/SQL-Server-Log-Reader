using BonikPOS.VoiceSearch.Core.Medicine;

namespace BonikPOS.VoiceSearch.MedicineSearch.Text;

/// <summary>
/// Splits a recognised or typed query into the medicine name, strengths (number + unit, "per" combinations), bare
/// numbers ("Napa 500"), dosage forms and ignorable words (quantities such as "two strips", fillers such as "দিন").
/// </summary>
public static class QueryParser
{
    public static ParsedMedicineQuery Parse(string text)
    {
        string normalized = TextNormalizer.Normalize(text);
        var tokens = TextNormalizer.Tokenize(normalized);
        var name = new List<string>();
        var nameWithNumbers = new List<string>();
        var nameWithForms = new List<string>();
        var strengths = new List<StrengthValue>();
        var bare = new List<decimal>();
        var forms = new List<DosageFormCategory>();
        int i = 0;
        while (i < tokens.Length)
        {
            string t = tokens[i];
            if (t is "/" or "+")
            {
                i++;
                continue;
            }

            if (NumberWords.TryParse(tokens, i, out decimal value, out int used))
            {
                int j = i + used;
                if (MedicineVocabulary.TryUnit(tokens, j, out var unit, out int unitTokens))
                {
                    strengths.Add(Normalize(value, unit));
                    j += unitTokens;
                    // "120 mg per 5 ml" / "120 mg / 5 ml"
                    if (j < tokens.Length && tokens[j] is "per" or "/" or "প্রতি" &&
                        NumberWords.TryParse(tokens, j + 1, out decimal perValue, out int perUsed) &&
                        MedicineVocabulary.TryUnit(tokens, j + 1 + perUsed, out var perUnit, out int perUnitTokens))
                    {
                        strengths.Add(Normalize(perValue, perUnit));
                        j += 1 + perUsed + perUnitTokens;
                    }

                    i = j;
                    continue;
                }

                if (j < tokens.Length && MedicineVocabulary.IsQuantityWord(tokens[j]))
                {
                    i = j + 1; // "two strips", "দুই পাতা": a quantity, not a strength
                    continue;
                }

                bare.Add(value);
                nameWithNumbers.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                i = j;
                continue;
            }

            if (MedicineVocabulary.TryUnit(tokens, i, out _, out int strayUnit))
            {
                i += strayUnit; // unit without a number (e.g. "per ml"): no information for matching
                continue;
            }

            if (MedicineVocabulary.TryForm(t, out var form))
            {
                if (!forms.Contains(form))
                {
                    forms.Add(form);
                }

                nameWithForms.Add(t); // kept for names split into a form word by the recogniser ("sir gel")
                i++;
                continue;
            }

            if (MedicineVocabulary.IsStopWord(t) || MedicineVocabulary.IsQuantityWord(t))
            {
                i++;
                continue;
            }

            name.Add(t);
            nameWithNumbers.Add(t);
            nameWithForms.Add(t);
            i++;
        }

        string nameText = string.Join(' ', name);
        return new ParsedMedicineQuery(
            text, normalized, nameText, PhoneticKey.For(nameText), strengths, bare, forms, TextNormalizer.ContainsBengali(normalized),
            string.Join(' ', nameWithNumbers), name.Count > 0 && forms.Count > 0 ? string.Join(' ', nameWithForms) : string.Empty);
    }

    /// <summary>Grams are converted to milligrams so that "1 g" equals "1000 mg".</summary>
    public static StrengthValue Normalize(decimal value, StrengthUnit unit) =>
        unit == StrengthUnit.Gram ? new StrengthValue(value * 1000, StrengthUnit.Milligram) : new StrengthValue(value, unit);
}

/// <summary>Parses catalog strength strings: "500 mg", "120 mg/5 ml", "500 mg + 65 mg", "0.05%", "100 mcg/puff", "1 g".</summary>
public static class StrengthParser
{
    public static IReadOnlyList<StrengthValue> Parse(string? strength)
    {
        var result = new List<StrengthValue>();
        if (string.IsNullOrWhiteSpace(strength))
        {
            return result;
        }

        var tokens = TextNormalizer.Tokenize(TextNormalizer.Normalize(strength));
        for (int i = 0; i < tokens.Length; i++)
        {
            if (decimal.TryParse(tokens[i], System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var v)
                && MedicineVocabulary.TryUnit(tokens, i + 1, out var unit, out int used))
            {
                result.Add(QueryParser.Normalize(v, unit));
                i += used;
            }
        }

        return result;
    }
}
