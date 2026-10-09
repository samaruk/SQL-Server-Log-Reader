using System.Globalization;

namespace BonikPOS.VoiceSearch.MedicineSearch.Text;

/// <summary>
/// Parses spoken numbers in English and Bangla as they appear in ASR output: "five hundred", "one twenty" (=120),
/// "two fifty" (=250), "point five", "পাঁচশো", "একশো বিশ", "আড়াইশো" (=250), "দেড়শো" (=150), and digits.
/// </summary>
public static class NumberWords
{
    private static readonly Dictionary<string, int> EnglishUnits = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> EnglishTens = new(StringComparer.Ordinal)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fourty"] = 40, ["fifty"] = 50, ["sixty"] = 60,
        ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    // Bangla 0-100: every number has its own word. Common spoken/ASR spelling variants are included.
    private static readonly Dictionary<string, decimal> Bangla = Build(
        ("শূন্য", 0), ("এক", 1), ("দুই", 2), ("দু", 2), ("তিন", 3), ("চার", 4), ("পাঁচ", 5), ("পাচ", 5), ("ছয়", 6), ("ছয়", 6),
        ("সাত", 7), ("আট", 8), ("নয়", 9), ("নয়", 9), ("দশ", 10), ("এগারো", 11), ("বারো", 12), ("তেরো", 13),
        ("চৌদ্দ", 14), ("পনেরো", 15), ("পনের", 15), ("ষোল", 16), ("ষোলো", 16), ("সতেরো", 17), ("আঠারো", 18), ("উনিশ", 19),
        ("বিশ", 20), ("কুড়ি", 20), ("একুশ", 21), ("বাইশ", 22), ("তেইশ", 23), ("চব্বিশ", 24), ("পঁচিশ", 25), ("পচিশ", 25),
        ("ছাব্বিশ", 26), ("সাতাশ", 27), ("আটাশ", 28), ("উনত্রিশ", 29), ("ত্রিশ", 30), ("একত্রিশ", 31), ("বত্রিশ", 32),
        ("তেত্রিশ", 33), ("চৌত্রিশ", 34), ("পঁয়ত্রিশ", 35), ("ছত্রিশ", 36), ("সাঁইত্রিশ", 37), ("আটত্রিশ", 38),
        ("উনচল্লিশ", 39), ("চল্লিশ", 40), ("একচল্লিশ", 41), ("বিয়াল্লিশ", 42), ("তেতাল্লিশ", 43), ("চুয়াল্লিশ", 44),
        ("পঁয়তাল্লিশ", 45), ("ছেচল্লিশ", 46), ("সাতচল্লিশ", 47), ("আটচল্লিশ", 48), ("উনপঞ্চাশ", 49), ("পঞ্চাশ", 50),
        ("একান্ন", 51), ("বায়ান্ন", 52), ("তিপ্পান্ন", 53), ("চুয়ান্ন", 54), ("পঞ্চান্ন", 55), ("ছাপ্পান্ন", 56),
        ("সাতান্ন", 57), ("আটান্ন", 58), ("উনষাট", 59), ("ষাট", 60), ("একষট্টি", 61), ("বাষট্টি", 62), ("তেষট্টি", 63),
        ("চৌষট্টি", 64), ("পঁয়ষট্টি", 65), ("ছেষট্টি", 66), ("সাতষট্টি", 67), ("আটষট্টি", 68), ("উনসত্তর", 69),
        ("সত্তর", 70), ("একাত্তর", 71), ("বাহাত্তর", 72), ("তিয়াত্তর", 73), ("চুয়াত্তর", 74), ("পঁচাত্তর", 75),
        ("ছিয়াত্তর", 76), ("সাতাত্তর", 77), ("আটাত্তর", 78), ("উনআশি", 79), ("আশি", 80), ("একাশি", 81), ("বিরাশি", 82),
        ("তিরাশি", 83), ("চুরাশি", 84), ("পঁচাশি", 85), ("ছিয়াশি", 86), ("সাতাশি", 87), ("অষ্টআশি", 88), ("অষ্টাশি", 88),
        ("উননব্বই", 89), ("নব্বই", 90), ("একানব্বই", 91), ("বিরানব্বই", 92), ("তিরানব্বই", 93), ("চুরানব্বই", 94),
        ("পঁচানব্বই", 95), ("ছিয়ানব্বই", 96), ("সাতানব্বই", 97), ("আটানব্বই", 98), ("নিরানব্বই", 99),
        ("দেড়", 1.5m), ("আড়াই", 2.5m));

    // English number words as Bangla speech recognition writes them in Bengali script ("ফাইভ হান্ড্রেড").
    private static readonly Dictionary<string, string> EnglishInBengaliScript = new Dictionary<string, string>
    {
        ["জিরো"] = "zero", ["ওয়ান"] = "one", ["ওয়ান"] = "one", ["টু"] = "two", ["থ্রি"] = "three", ["ফোর"] = "four",
        ["ফাইভ"] = "five", ["সিক্স"] = "six", ["সেভেন"] = "seven", ["এইট"] = "eight", ["নাইন"] = "nine", ["টেন"] = "ten",
        ["ইলেভেন"] = "eleven", ["টুয়েলভ"] = "twelve", ["টুয়েলভ"] = "twelve", ["ফিফটিন"] = "fifteen", ["টুয়েন্টি"] = "twenty",
        ["টুয়েন্টি"] = "twenty", ["টোয়েন্টি"] = "twenty", ["থার্টি"] = "thirty", ["ফোর্টি"] = "forty", ["ফিফটি"] = "fifty",
        ["সিক্সটি"] = "sixty", ["সেভেনটি"] = "seventy", ["এইটি"] = "eighty", ["নাইনটি"] = "ninety",
        ["হান্ড্রেড"] = "hundred", ["হানড্রেড"] = "hundred", ["থাউজেন্ড"] = "thousand", ["পয়েন্ট"] = "point", ["পয়েন্ট"] = "point",
    }.Aggregate(new Dictionary<string, string>(StringComparer.Ordinal), (d, p) =>
    {
        d[p.Key.Normalize(System.Text.NormalizationForm.FormC)] = p.Value; // spelling variants may coincide after NFC
        return d;
    });

    // Romanised Bangla numbers typed in Banglish ("dui pata", "pach sho").
    private static readonly Dictionary<string, decimal> RomanizedBangla = new(StringComparer.Ordinal)
    {
        ["ek"] = 1, ["dui"] = 2, ["du"] = 2, ["tin"] = 3, ["char"] = 4, ["pach"] = 5, ["panch"] = 5, ["choy"] = 6, ["chhoy"] = 6,
        ["sat"] = 7, ["aat"] = 8, ["noy"] = 9, ["dosh"] = 10, ["bish"] = 20, ["pochish"] = 25, ["trish"] = 30,
        ["chollish"] = 40, ["ponchash"] = 50, ["pongchash"] = 50, ["eksho"] = 100, ["dedh"] = 1.5m, ["arai"] = 2.5m,
    };

    private static readonly string[] BanglaHundred = ["শো", "শ", "শত", "শ’"];
    private static readonly string[] BanglaThousand = ["হাজার"];

    /// <summary>
    /// Tries to read a number starting at <paramref name="start"/>. Returns the value and how many tokens it used.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> tokens, int start, out decimal value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (start >= tokens.Count)
        {
            return false;
        }

        if (TryDigits(tokens, start, out value, out consumed))
        {
            return true;
        }

        if (TryEnglish(tokens, start, out value, out consumed))
        {
            return true;
        }

        if (EnglishInBengaliScript.ContainsKey(tokens[start]))
        {
            var mapped = tokens.Select(t => EnglishInBengaliScript.TryGetValue(t, out var e) ? e : t).ToList();
            if (TryEnglish(mapped, start, out value, out consumed))
            {
                return true;
            }
        }

        if (TryBangla(tokens, start, out value, out consumed))
        {
            return true;
        }

        return TryRomanizedBangla(tokens, start, out value, out consumed);
    }

    private static bool TryRomanizedBangla(IReadOnlyList<string> tokens, int start, out decimal value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (!RomanizedBangla.TryGetValue(tokens[start], out var v))
        {
            return false;
        }

        consumed = 1;
        if (start + 1 < tokens.Count && tokens[start + 1] is "sho" or "shoto" or "sot")
        {
            v *= 100;
            consumed = 2;
        }

        value = v;
        return true;
    }

    private static bool TryDigits(IReadOnlyList<string> tokens, int start, out decimal value, out int consumed)
    {
        consumed = 0;
        if (!decimal.TryParse(tokens[start], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        consumed = 1;
        // "0 point 5" style is unusual with digits; "500" is complete.
        return true;
    }

    private static bool TryEnglish(IReadOnlyList<string> tokens, int start, out decimal value, out int consumed)
    {
        value = 0;
        consumed = 0;
        decimal total = 0;
        decimal current = 0;
        bool any = false;
        bool unitAllowed = true; // a unit word may start the number or follow "hundred" / "thousand" / "and"
        int i = start;
        while (i < tokens.Count)
        {
            string t = tokens[i];
            if (EnglishUnits.TryGetValue(t, out int u))
            {
                if (!unitAllowed)
                {
                    break; // "one two" is two numbers, not three
                }

                current += u;
                any = true;
                unitAllowed = false;
                i++;
                continue;
            }

            if (EnglishTens.TryGetValue(t, out int tens))
            {
                // "one twenty" / "two fifty": a leading digit followed by a tens word means hundreds.
                if (current is > 0 and < 10 && total == 0 && i > start && !tokens[i - 1].Equals("hundred", StringComparison.Ordinal))
                {
                    current = current * 100 + tens;
                }
                else
                {
                    current += tens;
                }

                any = true;
                unitAllowed = false;
                i++;
                if (i < tokens.Count && EnglishUnits.TryGetValue(tokens[i], out int unit) && unit is > 0 and < 10)
                {
                    current += unit;
                    i++;
                }

                continue;
            }

            if (t is "hundred" && any)
            {
                current = (current == 0 ? 1 : current) * 100;
                unitAllowed = true;
                i++;
                continue;
            }

            if (t is "thousand" && any)
            {
                total += (current == 0 ? 1 : current) * 1000;
                current = 0;
                unitAllowed = true;
                i++;
                continue;
            }

            if (t is "and" && any && i + 1 < tokens.Count && (EnglishUnits.ContainsKey(tokens[i + 1]) || EnglishTens.ContainsKey(tokens[i + 1])))
            {
                unitAllowed = true;
                i++;
                continue;
            }

            if (t is "point" or "dot" && i + 1 < tokens.Count && EnglishUnits.TryGetValue(tokens[i + 1], out int frac) && frac < 10)
            {
                decimal whole = total + current;
                decimal scale = 0.1m;
                decimal fraction = 0;
                i++;
                while (i < tokens.Count && EnglishUnits.TryGetValue(tokens[i], out int d) && d < 10)
                {
                    fraction += d * scale;
                    scale /= 10;
                    i++;
                }

                value = whole + fraction;
                consumed = i - start;
                return true;
            }

            break;
        }

        if (!any)
        {
            return false;
        }

        value = total + current;
        consumed = i - start;
        return true;
    }

    private static bool TryBangla(IReadOnlyList<string> tokens, int start, out decimal value, out int consumed)
    {
        value = 0;
        consumed = 0;
        decimal total = 0;
        int i = start;
        bool any = false;
        while (i < tokens.Count)
        {
            string t = tokens[i];
            if (TrySplitMultiplier(t, out decimal head, out decimal multiplier))
            {
                total += head * multiplier;
                any = true;
                i++;
                continue;
            }

            if (Bangla.TryGetValue(StripCandrabindu(t), out decimal v))
            {
                // A separate multiplier word may follow: "পাঁচ শো".
                if (i + 1 < tokens.Count && (BanglaHundred.Contains(tokens[i + 1]) || BanglaThousand.Contains(tokens[i + 1])))
                {
                    total += v * (BanglaHundred.Contains(tokens[i + 1]) ? 100 : 1000);
                    i += 2;
                }
                else
                {
                    total += v;
                    i++;
                }

                any = true;
                if (v < 100 && total % 100 != 0 && i > start)
                {
                    break; // a final unit/tens word ends the number ("একশো বিশ" = 120)
                }

                continue;
            }

            if (t is "দশমিক" or "পয়েন্ট" or "পয়েন্ট" && any && i + 1 < tokens.Count && Bangla.TryGetValue(StripCandrabindu(tokens[i + 1]), out decimal d) && d < 10)
            {
                total += d / 10;
                i += 2;
                break;
            }

            break;
        }

        if (!any)
        {
            return false;
        }

        value = total;
        consumed = i - start;
        return true;
    }

    /// <summary>"পাঁচশো" -> (5, 100), "দুই হাজার" handled by the caller, "আড়াইশো" -> (2.5, 100).</summary>
    private static bool TrySplitMultiplier(string token, out decimal head, out decimal multiplier)
    {
        head = 0;
        multiplier = 0;
        foreach (var (suffix, mult) in new[] { ("হাজার", 1000m), ("শত", 100m), ("শো", 100m), ("শ", 100m) })
        {
            if (token.Length > suffix.Length && token.EndsWith(suffix, StringComparison.Ordinal))
            {
                string stem = StripCandrabindu(token[..^suffix.Length]);
                if (Bangla.TryGetValue(stem, out head))
                {
                    multiplier = mult;
                    return true;
                }
            }
        }

        if (token is "একশো" or "একশ" or "শো")
        {
            head = 1;
            multiplier = 100;
            return true;
        }

        return false;
    }

    private static string StripCandrabindu(string s) => s.Replace("ঁ", string.Empty, StringComparison.Ordinal);

    private static Dictionary<string, decimal> Build(params (string Word, decimal Value)[] entries)
    {
        var d = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (word, v) in entries)
        {
            d[StripCandrabindu(word.Normalize(System.Text.NormalizationForm.FormC))] = v;
        }

        return d;
    }
}
