using System.Text;

namespace BonikPOS.VoiceSearch.MedicineSearch.Text;

/// <summary>
/// Bengali script -> Latin transliteration tuned for how medicine brand names (mostly English loanwords) are written in
/// Bangla. The inherent vowel is normally NOT emitted (loanword spellings rarely pronounce it: "সেকলো" -> "seklo"), with
/// two exceptions where Bangla pronunciation keeps it: a word-initial consonant directly followed by another consonant
/// ("মন্টেয়ার" -> "monteyar", "কমেট" -> "komet") and the consonant before a bare word-final consonant ("নেবানল" ->
/// "nebanol"). The phonetic similarity treats vowel differences as cheap edits. Ya-phala + aa ("ক্যা") becomes "a" as in
/// English "cat". Input must be NFC-normalised (NFC decomposes ড়/ঢ়/য় into base + nukta, which is handled here).
/// </summary>
public static class BanglaTransliterator
{
    private const char Virama = '্';
    private const char Nukta = '়';
    private const char YaPhalaBase = 'য';   // য
    private const char AaSign = 'া';         // া

    private static readonly Dictionary<char, string> Consonants = new()
    {
        ['ক'] = "k", ['খ'] = "kh", ['গ'] = "g", ['ঘ'] = "gh", ['ঙ'] = "ng",
        ['চ'] = "ch", ['ছ'] = "ch", ['জ'] = "j", ['ঝ'] = "jh", ['ঞ'] = "n",
        ['ট'] = "t", ['ঠ'] = "th", ['ড'] = "d", ['ঢ'] = "dh", ['ণ'] = "n",
        ['ত'] = "t", ['থ'] = "th", ['দ'] = "d", ['ধ'] = "dh", ['ন'] = "n",
        ['প'] = "p", ['ফ'] = "f", ['ব'] = "b", ['ভ'] = "bh", ['ম'] = "m",
        ['য'] = "j", ['র'] = "r", ['ল'] = "l", ['শ'] = "sh", ['ষ'] = "sh",
        ['স'] = "s", ['হ'] = "h", ['ৎ'] = "t", ['\u09DC'] = "r", ['\u09DD'] = "rh", ['\u09DF'] = "y",
    };

    private static readonly Dictionary<char, string> IndependentVowels = new()
    {
        ['অ'] = "o", ['আ'] = "a", ['ই'] = "i", ['ঈ'] = "i", ['উ'] = "u", ['ঊ'] = "u",
        ['ঋ'] = "ri", ['এ'] = "e", ['ঐ'] = "oi", ['ও'] = "o", ['ঔ'] = "ou",
    };

    private static readonly Dictionary<char, string> VowelSigns = new()
    {
        ['া'] = "a", ['ি'] = "i", ['ী'] = "i", ['ু'] = "u", ['ূ'] = "u", ['ৃ'] = "ri",
        ['ে'] = "e", ['ৈ'] = "oi", ['ো'] = "o", ['ৌ'] = "ou",
    };

    public static string Transliterate(string text)
    {
        if (!TextNormalizer.ContainsBengali(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            // Nukta forms produced by NFC decomposition: ড + ় = ড়, ঢ + ় = ঢ়, য + ় = য়.
            if (next == Nukta && c is 'ড' or 'ঢ' or 'য')
            {
                sb.Append(c switch { 'ড' => "r", 'ঢ' => "rh", _ => "y" });
                i++;
                continue;
            }

            if (Consonants.TryGetValue(c, out var consonant))
            {
                // Ya-phala (consonant + virama + য): "ক্যা" sounds like English "ka(t)"; plain ya-phala is silent.
                if (next == Virama && i + 2 < text.Length && text[i + 2] == YaPhalaBase && c != 'র')
                {
                    sb.Append(consonant);
                    i += 2;
                    if (i + 1 < text.Length && text[i + 1] == AaSign)
                    {
                        sb.Append('a');
                        i++;
                    }

                    continue;
                }

                sb.Append(consonant);
                if (KeepsInherentVowel(text, i))
                {
                    sb.Append('o');
                }

                continue;
            }

            if (IndependentVowels.TryGetValue(c, out var vowel))
            {
                // "অ্যা" / "এ্যা" at the start of loanwords: "অ্যান্টাসিড" -> "antasid".
                if (c is 'অ' or 'এ' && next == Virama && i + 3 < text.Length && text[i + 2] == YaPhalaBase && text[i + 3] == AaSign)
                {
                    sb.Append('a');
                    i += 3;
                    continue;
                }

                sb.Append(vowel);
                continue;
            }

            if (VowelSigns.TryGetValue(c, out var sign))
            {
                sb.Append(sign);
                continue;
            }

            switch (c)
            {
                case Virama:
                case 'ঁ': // candrabindu (nasalisation)
                case 'ঽ': // avagraha
                case 'ৗ': // au length mark
                case Nukta:
                    continue;
                case 'ং': // anusvara
                    sb.Append("ng");
                    continue;
                case 'ঃ': // visarga
                    sb.Append('h');
                    continue;
            }

            sb.Append(c is >= 'ঀ' and <= '৿' ? ' ' : c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when the bare consonant at <paramref name="i"/> (no vowel sign, no virama, no nukta after it) is pronounced
    /// with its inherent vowel: word-initial and followed by a consonant, or followed by a bare word-final consonant.
    /// </summary>
    private static bool KeepsInherentVowel(string text, int i)
    {
        int next = i + 1;
        if (next >= text.Length || !IsConsonant(text[next]))
        {
            return false; // vowel sign, virama, nukta, end of word or non-Bengali text follows
        }

        bool wordInitial = i == 0 || !IsBengaliLetterOrMark(text[i - 1]);
        if (wordInitial)
        {
            return true;
        }

        // Next consonant is the last letter of the word and carries no vowel sign / virama / nukta.
        int after = next + 1;
        return after >= text.Length || !IsBengaliLetterOrMark(text[after]);
    }

    private static bool IsConsonant(char c) => Consonants.ContainsKey(c);

    private static bool IsBengaliLetterOrMark(char c) => c is >= 'ঀ' and <= '৿' && c is not (>= '০' and <= '৯');
}