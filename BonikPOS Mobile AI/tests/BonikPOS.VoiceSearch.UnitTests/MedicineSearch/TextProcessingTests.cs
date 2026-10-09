using System.Text;
using BonikPOS.VoiceSearch.MedicineSearch.Text;

namespace BonikPOS.VoiceSearch.UnitTests.MedicineSearch;

public sealed class TextNormalizerTests
{
    [Theory]
    [InlineData("500mg", "500 mg")]
    [InlineData("Napa500MG", "napa 500 mg")]
    [InlineData("  NAPA   Extra\t500 mg ", "napa extra 500 mg")]
    [InlineData("120mg/5ml", "120 mg / 5 ml")]
    [InlineData("0.05%", "0.05 %")]
    [InlineData("Napa. Extra, 2.5 mg!", "napa extra 2.5 mg")] // sentence punctuation dropped, decimal point kept
    [InlineData("Cef-3", "cef 3")]
    [InlineData("500 + 65", "500 + 65")]
    [InlineData("৫০০ মিগ্রা", "500 মিগ্রা")]                   // Bengali digits -> ASCII
    [InlineData("নাপা৫০০", "নাপা 500")]                         // and split from Bengali letters
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalize_ProducesTheCanonicalMatchingForm(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_RemovesZeroWidthCharacters()
    {
        string withJoiners = "সে‌ক‍লো﻿";
        Assert.Equal("সেকলো", TextNormalizer.Normalize(withJoiners));
        Assert.Equal("napa", TextNormalizer.Normalize("na‌pa‍"));
    }

    [Fact]
    public void Normalize_AppliesNfc_SoComposedAndDecomposedSpellingsAreEqual()
    {
        // ো (U+09CB) is canonically equivalent to ে + া (U+09C7 U+09BE).
        string composed = "সেকলো";
        string decomposed = composed.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(composed, decomposed);
        Assert.Equal(TextNormalizer.Normalize(composed), TextNormalizer.Normalize(decomposed));
        Assert.True(TextNormalizer.Normalize(decomposed).IsNormalized(NormalizationForm.FormC));

        string latinDecomposed = "Café"; // e + combining acute
        Assert.Equal("café", TextNormalizer.Normalize(latinDecomposed));
    }

    [Fact]
    public void Compact_RemovesSpacesAndPunctuation()
    {
        Assert.Equal("cef3", TextNormalizer.Compact(TextNormalizer.Normalize("Cef-3")));
        Assert.Equal("cef3", TextNormalizer.Compact(TextNormalizer.Normalize("cef 3")));
        Assert.Equal("নাপাএক্সট্রা", TextNormalizer.Compact(TextNormalizer.Normalize("নাপা এক্সট্রা")));
    }
}

public sealed class PhoneticTests
{
    private static string Key(string text) => PhoneticKey.For(TextNormalizer.Normalize(text));

    [Theory]
    [InlineData("সেকলো", "seklo")]
    [InlineData("নাপা", "napa")]
    [InlineData("সিপ্রোসিন", "siprosin")]
    [InlineData("অ্যান্টাসিড", "antasid")]
    [InlineData("ক্যাপসুল", "kapsul")]
    public void BanglaTransliteration_DropsTheInherentVowel_AndHandlesYaPhala(string bangla, string expected)
    {
        Assert.Equal(expected, BanglaTransliterator.Transliterate(TextNormalizer.Normalize(bangla)));
    }

    [Fact]
    public void Transliterate_LeavesLatinTextUnchanged()
    {
        Assert.Equal("napa extra", BanglaTransliterator.Transliterate("napa extra"));
    }

    [Theory]
    [InlineData("Seclo", "seklo", "সেকলো")]
    [InlineData("Napa", "napa", "নাপা")]
    [InlineData("Ciprocin", "siprosin", "সিপ্রোসিন")]
    [InlineData("Fexo", "fekso", "ফেক্সো")]
    public void EnglishBanglishAndBanglaSpellings_ShareOnePhoneticKey(string english, string banglish, string bangla)
    {
        string k = Key(english);
        Assert.Equal(k, Key(banglish));
        Assert.Equal(k, Key(bangla));
        Assert.Equal(1.0, PhoneticSimilarity.Similarity(Key(english), Key(bangla)));
    }

    [Theory]
    [InlineData("Maxpro", "ম্যাক্সপ্রো")]
    [InlineData("Alatrol", "অ্যালাট্রল")]   // Bangla spelling has no vowel between r and l
    [InlineData("Sergel", "সার্জেল")]       // e/a and g/j differ
    [InlineData("Ceevit", "সিভিট")]
    [InlineData("Seclo", "seclow")]
    public void CloseSpellings_AreVerySimilar(string a, string b)
    {
        double s = PhoneticSimilarity.Similarity(Key(a), Key(b));
        Assert.True(s >= 0.8, $"{Key(a)} vs {Key(b)}: {s}");
    }

    [Theory]
    [InlineData("Seclo", "Napa")]
    [InlineData("Fexo", "Monas")]
    [InlineData("Sergel", "Zimax")]
    public void DifferentNames_AreNotSimilar(string a, string b)
    {
        double s = PhoneticSimilarity.Similarity(Key(a), Key(b));
        Assert.True(s < 0.5, $"{Key(a)} vs {Key(b)}: {s}");
    }

    [Fact]
    public void PhoneticKey_FoldsSpellingVariants()
    {
        Assert.Equal(Key("phexo"), Key("fexo"));       // ph -> f
        Assert.Equal(Key("zimax"), Key("jimaks"));     // z -> j, x -> ks
        Assert.Equal(Key("vitamin"), Key("bitamin"));  // v -> b
        Assert.Equal(Key("allatrol"), Key("alatrol")); // doubled letters collapse
        Assert.Equal("", Key(""));
    }

    [Fact]
    public void Similarity_IsSymmetric_AndBoundedBetweenZeroAndOne()
    {
        string[] words = ["seklo", "napa", "siprosin", "maksplo", "a", "zzzzzz"];
        foreach (var a in words)
        {
            foreach (var b in words)
            {
                double ab = PhoneticSimilarity.Similarity(a, b);
                Assert.InRange(ab, 0, 1);
                Assert.Equal(ab, PhoneticSimilarity.Similarity(b, a), 1e-12);
            }
        }

        Assert.Equal(1, PhoneticSimilarity.Similarity("", ""));
        Assert.Equal(0, PhoneticSimilarity.Similarity("napa", ""));
        Assert.Equal(1 - 1.0 / 5, PhoneticSimilarity.SpellingSimilarity("seclo", "secl0"), 1e-12);
    }

    [Fact]
    public void VowelEditsCostLessThanConsonantEdits()
    {
        Assert.True(PhoneticSimilarity.Distance("seklo", "sekla") < PhoneticSimilarity.Distance("seklo", "sekto"));
        Assert.True(PhoneticSimilarity.Distance("seklo", "sklo") < PhoneticSimilarity.Distance("seklo", "selo"));
        Assert.True(PhoneticSimilarity.Distance("napa", "naba") < PhoneticSimilarity.Distance("napa", "nasa")); // p/b related
    }
}

public sealed class NumberWordsTests
{
    private static (decimal Value, int Consumed) Parse(string text)
    {
        var tokens = TextNormalizer.Tokenize(TextNormalizer.Normalize(text));
        Assert.True(NumberWords.TryParse(tokens, 0, out var value, out int consumed), $"'{text}' was not parsed");
        return (value, consumed);
    }

    [Theory]
    [InlineData("five hundred", 500, 2)]
    [InlineData("two hundred fifty", 250, 3)]
    [InlineData("two hundred and fifty", 250, 4)]
    [InlineData("one twenty", 120, 2)]       // spoken strength shorthand
    [InlineData("two fifty", 250, 2)]
    [InlineData("twenty five", 25, 2)]
    [InlineData("one thousand", 1000, 2)]
    [InlineData("point five", 0.5, 2)]
    [InlineData("zero point two five", 0.25, 4)]
    [InlineData("ten", 10, 1)]
    public void English(string text, double expected, int consumed)
    {
        var (value, used) = Parse(text);
        Assert.Equal((decimal)expected, value);
        Assert.Equal(consumed, used);
    }

    [Theory]
    [InlineData("পাঁচশো", 500, 1)]
    [InlineData("পাঁচ শো", 500, 2)]
    [InlineData("দুইশো পঞ্চাশ", 250, 2)]
    [InlineData("একশো বিশ", 120, 2)]
    [InlineData("আড়াইশো", 250, 1)]
    [InlineData("দেড়শো", 150, 1)]
    [InlineData("বিশ", 20, 1)]
    [InlineData("পঁচিশ", 25, 1)]
    [InlineData("দুই হাজার", 2000, 2)]
    [InlineData("ফাইভ হান্ড্রেড", 500, 2)] // English number words written in Bengali script
    [InlineData("pach sho", 500, 2)]          // Banglish
    public void Bangla(string text, double expected, int consumed)
    {
        var (value, used) = Parse(text);
        Assert.Equal((decimal)expected, value);
        Assert.Equal(consumed, used);
    }

    [Theory]
    [InlineData("500", 500)]
    [InlineData("৫০০", 500)]
    [InlineData("২৫০", 250)]
    [InlineData("0.5", 0.5)]
    [InlineData("১২.৫", 12.5)]
    public void Digits_IncludingBengaliDigits(string text, double expected)
    {
        Assert.Equal((decimal)expected, Parse(text).Value);
    }

    [Fact]
    public void ANumberEndsWhereItsWordsEnd()
    {
        var tokens = TextNormalizer.Tokenize("napa five hundred mg");
        Assert.False(NumberWords.TryParse(tokens, 0, out _, out _));
        Assert.True(NumberWords.TryParse(tokens, 1, out var v, out int used));
        Assert.Equal(500m, v);
        Assert.Equal(2, used);

        // "one two" is two numbers, not twelve or three.
        Assert.True(NumberWords.TryParse(["one", "two"], 0, out v, out used));
        Assert.Equal(1m, v);
        Assert.Equal(1, used);
        Assert.False(NumberWords.TryParse(["napa"], 0, out _, out _));
        Assert.False(NumberWords.TryParse([], 0, out _, out _));
    }
}
