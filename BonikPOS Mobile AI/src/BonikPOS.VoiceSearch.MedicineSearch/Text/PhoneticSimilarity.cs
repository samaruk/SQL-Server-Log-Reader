namespace BonikPOS.VoiceSearch.MedicineSearch.Text;

/// <summary>
/// Weighted optimal-string-alignment distance on <see cref="PhoneticKey"/> strings. Vowel insertions, deletions and
/// substitutions are cheap (Bangla spellings of loanwords drop or change vowels), substitutions between related
/// consonants (k/g, t/d, p/b/f, r/l, n/m, j/g) cost half, everything else costs one.
/// </summary>
public static class PhoneticSimilarity
{
    public const double VowelIndel = 0.4;
    public const double ConsonantIndel = 1.0;
    public const double VowelSubstitution = 0.35;
    public const double RelatedSubstitution = 0.5;
    public const double Transposition = 0.6;

    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return a.Length == b.Length ? 1 : 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        double max = Math.Max(Weight(a), Weight(b));
        return Math.Clamp(1 - Distance(a, b) / max, 0, 1);
    }

    public static double Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int n = a.Length, m = b.Length;
        int width = m + 1;
        Span<double> d = (n + 1) * width <= 4096 ? stackalloc double[(n + 1) * width] : new double[(n + 1) * width];
        d[0] = 0;
        for (int i = 1; i <= n; i++)
        {
            d[i * width] = d[(i - 1) * width] + Indel(a[i - 1]);
        }

        for (int j = 1; j <= m; j++)
        {
            d[j] = d[j - 1] + Indel(b[j - 1]);
        }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                double best = Math.Min(
                    d[(i - 1) * width + j] + Indel(a[i - 1]),
                    d[i * width + j - 1] + Indel(b[j - 1]));
                best = Math.Min(best, d[(i - 1) * width + j - 1] + Substitution(a[i - 1], b[j - 1]));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1] && a[i - 1] != a[i - 2])
                {
                    best = Math.Min(best, d[(i - 2) * width + j - 2] + Transposition);
                }

                d[i * width + j] = best;
            }
        }

        return d[n * width + m];
    }

    /// <summary>Plain Levenshtein similarity on spellings, for typed queries with typos.</summary>
    public static double SpellingSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return a.Length == b.Length ? 1 : 0;
        }

        int n = a.Length, m = b.Length;
        Span<int> prev = m + 1 <= 256 ? stackalloc int[m + 1] : new int[m + 1];
        Span<int> cur = m + 1 <= 256 ? stackalloc int[m + 1] : new int[m + 1];
        for (int j = 0; j <= m; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }

            cur.CopyTo(prev);
        }

        return 1 - (double)prev[m] / Math.Max(n, m);
    }

    public static double Weight(string s)
    {
        double w = 0;
        foreach (char c in s)
        {
            w += Indel(c);
        }

        return w;
    }

    private static double Indel(char c) => PhoneticKey.IsVowel(c) ? VowelIndel : ConsonantIndel;

    private static double Substitution(char x, char y)
    {
        if (x == y)
        {
            return 0;
        }

        bool vx = PhoneticKey.IsVowel(x), vy = PhoneticKey.IsVowel(y);
        if (vx && vy)
        {
            return VowelSubstitution;
        }

        if (vx || vy)
        {
            return 1.0;
        }

        return Related(x, y) ? RelatedSubstitution : 1.0;
    }

    private static bool Related(char x, char y)
    {
        static bool Pair(char a, char b, char p, char q) => (a == p && b == q) || (a == q && b == p);
        return Pair(x, y, 'k', 'g') || Pair(x, y, 'k', 'C') || Pair(x, y, 'g', 'j') || Pair(x, y, 't', 'd')
            || Pair(x, y, 'p', 'b') || Pair(x, y, 'p', 'f') || Pair(x, y, 'b', 'f') || Pair(x, y, 'r', 'l')
            || Pair(x, y, 'n', 'm') || Pair(x, y, 's', 'C') || Pair(x, y, 'j', 'C') || Pair(x, y, 'r', 'd');
    }
}
