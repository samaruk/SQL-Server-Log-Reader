using System.Globalization;
using System.Text;

namespace BonikPOS.VoiceSearch.MedicineSearch.Text;

/// <summary>
/// Canonical form for matching: Unicode NFC, lower case, Bengali digits converted to ASCII, zero-width joiners removed,
/// punctuation replaced by spaces (except the characters that carry strength information), digits separated from
/// letters ("500mg" -> "500 mg") and whitespace collapsed.
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string nfc = text.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(nfc.Length + 8);
        char prev = ' ';
        for (int i = 0; i < nfc.Length; i++)
        {
            char c = nfc[i];
            if (c is '‌' or '‍' or '﻿')
            {
                continue;
            }

            if (c is >= '০' and <= '৯')
            {
                c = (char)('0' + (c - '০'));
            }

            c = char.ToLowerInvariant(c);
            char next = i + 1 < nfc.Length ? nfc[i + 1] : ' ';
            bool keep = char.IsLetterOrDigit(c) || IsBengaliMark(c)
                || c is '%' or '/' or '+'
                || (c is '.' or ',' && char.IsDigit(prev) && char.IsDigit(next));
            if (c == ',')
            {
                keep = false; // thousands separators are not used in strengths
            }

            if (!keep)
            {
                c = ' ';
            }
            else if (c == '.' && !(char.IsDigit(prev) && char.IsDigit(next)))
            {
                c = ' ';
            }

            // Split digit/letter boundaries so units and numbers become separate tokens.
            if (sb.Length > 0 && c != ' ' && prev != ' ' &&
                ((char.IsDigit(c) && IsWordChar(prev)) || (IsWordChar(c) && char.IsDigit(prev)) || c is '%' or '/' or '+' || prev is '%' or '/' or '+'))
            {
                sb.Append(' ');
            }

            if (c == ' ' && (sb.Length == 0 || sb[^1] == ' '))
            {
                prev = c;
                continue;
            }

            sb.Append(c);
            prev = c;
        }

        return sb.ToString().Trim();
    }

    /// <summary>Normalized text without spaces, hyphens or slashes ("Cef-3" and "cef 3" both give "cef3").</summary>
    public static string Compact(string normalized)
    {
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            if (char.IsLetterOrDigit(c) || IsBengaliMark(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static string[] Tokenize(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool ContainsBengali(string text)
    {
        foreach (char c in text)
        {
            if (c is >= 'ঀ' and <= '৿')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Bengali combining marks are not "letters" for char.IsLetterOrDigit but are part of words.</summary>
    public static bool IsBengaliMark(char c) =>
        c is >= 'ঀ' and <= '৿' && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static bool IsWordChar(char c) => char.IsLetter(c) || IsBengaliMark(c);
}
