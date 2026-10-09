using System.Text;

namespace BonikPOS.VoiceSearch.MedicineSearch.Text;

/// <summary>
/// A pronunciation-oriented key shared by Latin catalog names, Banglish and transliterated Bangla, so that "Seclo",
/// "seklo", "সেকলো" and "Ciprocin"/"সিপ্রোসিন" ("siprosin") compare closely. Aspiration is dropped (kh, th, dh, bh),
/// ph->f, v->b, z->j, x->ks, q->k, soft c->s, hard c->k, ch->C, sh->s, y->i, doubled letters collapsed.
/// </summary>
public static class PhoneticKey
{
    public static string For(string normalizedText)
    {
        string latin = BanglaTransliterator.Transliterate(normalizedText);
        var src = new StringBuilder(latin.Length);
        foreach (char ch in latin)
        {
            char c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                src.Append(c);
            }
        }

        string s = src.ToString();
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            char n = i + 1 < s.Length ? s[i + 1] : '\0';
            switch (c)
            {
                case 'p' when n == 'h':
                    Append(sb, 'f');
                    i++;
                    continue;
                case 'c' when n == 'h':
                    Append(sb, 'C');
                    i++;
                    continue;
                case 's' when n == 'h':
                    Append(sb, 's');
                    i++;
                    continue;
                case 'c' when n == 'k':
                    Append(sb, 'k');
                    i++;
                    continue;
                case 'q':
                    Append(sb, 'k');
                    if (n == 'u')
                    {
                        i++;
                    }

                    continue;
                case 'c':
                    Append(sb, n is 'e' or 'i' or 'y' ? 's' : 'k');
                    continue;
                case 'x':
                    Append(sb, 'k');
                    Append(sb, 's');
                    continue;
                case 'z':
                    Append(sb, 'j');
                    continue;
                case 'v':
                    Append(sb, 'b');
                    continue;
                case 'w':
                    Append(sb, 'o');
                    continue;
                case 'y':
                    Append(sb, 'i');
                    continue;
                case 'h':
                    // Aspiration after a consonant (kh, gh, th, dh, bh, jh) and a trailing h are not distinctive.
                    if (sb.Length > 0 && !IsVowel(sb[^1]))
                    {
                        continue;
                    }

                    if (n == '\0')
                    {
                        continue;
                    }

                    Append(sb, 'h');
                    continue;
                default:
                    Append(sb, c);
                    continue;
            }
        }

        return sb.ToString();
    }

    public static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    private static void Append(StringBuilder sb, char c)
    {
        if (sb.Length > 0 && sb[^1] == c)
        {
            return; // collapse doubled letters ("ll", "ss", "kk")
        }

        sb.Append(c);
    }
}
