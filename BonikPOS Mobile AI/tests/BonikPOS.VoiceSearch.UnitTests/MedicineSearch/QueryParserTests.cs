using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.MedicineSearch.Text;

namespace BonikPOS.VoiceSearch.UnitTests.MedicineSearch;

public sealed class QueryParserTests
{
    private static StrengthValue Mg(decimal v) => new(v, StrengthUnit.Milligram);

    [Theory]
    [InlineData("Napa 500mg", 500, StrengthUnit.Milligram)]
    [InlineData("napa 500 milligram", 500, StrengthUnit.Milligram)]
    [InlineData("napa five hundred mg", 500, StrengthUnit.Milligram)]
    [InlineData("napa 500 m g", 500, StrengthUnit.Milligram)]
    [InlineData("নাপা ৫০০ মিলিগ্রাম", 500, StrengthUnit.Milligram)]
    [InlineData("নাপা পাঁচশো মিলিগ্রাম", 500, StrengthUnit.Milligram)]
    [InlineData("napa 1 g", 1000, StrengthUnit.Milligram)]            // grams are converted to milligrams
    [InlineData("napa 0.5 gram", 500, StrengthUnit.Milligram)]
    [InlineData("azmasol 100 mcg", 100, StrengthUnit.Microgram)]
    [InlineData("azmasol 100 microgram", 100, StrengthUnit.Microgram)]
    [InlineData("calbo d 200 IU", 200, StrengthUnit.InternationalUnit)]
    [InlineData("betnovate 0.1%", 0.1, StrengthUnit.Percent)]
    [InlineData("betnovate 1 percent", 1, StrengthUnit.Percent)]
    [InlineData("napa 5 ml", 5, StrengthUnit.Millilitre)]
    public void StrengthWithUnit_IsParsedAndNormalised(string query, double amount, StrengthUnit unit)
    {
        var parsed = QueryParser.Parse(query);

        Assert.Equal([new StrengthValue((decimal)amount, unit)], parsed.Strengths);
        Assert.Empty(parsed.BareNumbers);
    }

    [Theory]
    [InlineData("napa syrup 120 mg per 5 ml")]
    [InlineData("napa syrup 120mg/5ml")]
    [InlineData("napa syrup one twenty mg per five ml")]
    [InlineData("নাপা সিরাপ ১২০ মিলিগ্রাম প্রতি ৫ মিলি")]
    public void PerVolumeStrengths_YieldBothComponents(string query)
    {
        var parsed = QueryParser.Parse(query);

        Assert.Equal([Mg(120), new StrengthValue(5, StrengthUnit.Millilitre)], parsed.Strengths);
        Assert.Equal([DosageFormCategory.Syrup], parsed.Forms);
        Assert.Equal(PhoneticKey.For(TextNormalizer.Normalize(parsed.NameText)), parsed.NamePhoneticKey);
    }

    [Fact]
    public void BareNumbers_AreKeptSeparatelyFromStrengths()
    {
        var parsed = QueryParser.Parse("Napa extra 500");

        Assert.Empty(parsed.Strengths);
        Assert.Equal([500m], parsed.BareNumbers);
        Assert.Equal("napa extra", parsed.NameText);
        Assert.Equal("napa extra 500", parsed.NameWithNumbersText);
    }

    [Theory]
    [InlineData("seclo 20 capsule", DosageFormCategory.Capsule)]
    [InlineData("seclo 20 caps", DosageFormCategory.Capsule)]
    [InlineData("সেকলো ক্যাপসুল", DosageFormCategory.Capsule)]
    [InlineData("napa tablet", DosageFormCategory.Tablet)]
    [InlineData("napa tab", DosageFormCategory.Tablet)]
    [InlineData("নাপা ট্যাবলেট", DosageFormCategory.Tablet)]
    [InlineData("napa syrup", DosageFormCategory.Syrup)]
    [InlineData("নাপা সিরাপ", DosageFormCategory.Syrup)]
    [InlineData("napa suspension", DosageFormCategory.Suspension)]
    [InlineData("napa drops", DosageFormCategory.Drops)]
    [InlineData("ceftron injection", DosageFormCategory.Injection)]
    [InlineData("betnovate cream", DosageFormCategory.Cream)]
    [InlineData("seclo capsul", DosageFormCategory.Capsule)]      // near-miss spelling of a long form word
    public void DosageForms_AreRecognisedInEnglishAndBangla(string query, DosageFormCategory form)
    {
        var parsed = QueryParser.Parse(query);

        Assert.Equal([form], parsed.Forms);
        Assert.DoesNotContain(TextNormalizer.Tokenize(parsed.NameText), t => MedicineVocabulary.TryForm(t, out _));
    }

    [Theory]
    [InlineData("two strips of napa", "napa")]
    [InlineData("napa 500 mg two strips", "napa")]
    [InlineData("দুই পাতা নাপা", "নাপা")]
    [InlineData("napa dui pata", "napa")]
    [InlineData("3 boxes seclo", "seclo")]
    [InlineData("seclo ১০ পিস", "seclo")]
    public void Quantities_AreNotMistakenForStrengths(string query, string name)
    {
        var parsed = QueryParser.Parse(query);

        Assert.DoesNotContain(parsed.Strengths, s => s.Amount is 2 or 3 or 10);
        Assert.DoesNotContain(parsed.BareNumbers, n => n is 2 or 3 or 10);
        Assert.Equal(name, parsed.NameText);
    }

    [Theory]
    [InlineData("please give me napa", "napa")]
    [InlineData("do you have seclo in stock", "seclo")]
    [InlineData("napa ache ki bhai", "napa")]
    [InlineData("আমাকে নাপা দিন", "নাপা")]
    [InlineData("সেকলো আছে কি", "সেকলো")]
    [InlineData("Seclo twenty milligram capsule, two strips please", "seclo")]
    public void FillerWords_AreRemovedFromTheName(string query, string name)
    {
        Assert.Equal(name, QueryParser.Parse(query).NameText);
    }

    [Fact]
    public void BanglaScript_IsDetected_AndTheOriginalTextIsKept()
    {
        var parsed = QueryParser.Parse("নাপা এক্সট্রা পাঁচশো মিলিগ্রাম");

        Assert.True(parsed.ContainsBanglaScript);
        Assert.Equal("নাপা এক্সট্রা পাঁচশো মিলিগ্রাম", parsed.OriginalText);
        Assert.Equal("নাপা এক্সট্রা", parsed.NameText);
        Assert.Equal([Mg(500)], parsed.Strengths);
        Assert.Equal("napa ekstra", BanglaTransliterator.Transliterate(parsed.NameText));
        Assert.Equal(PhoneticKey.For("napa extra"), parsed.NamePhoneticKey); // same key as the English spelling
        Assert.False(QueryParser.Parse("napa").ContainsBanglaScript);
    }

    [Fact]
    public void QueryWithoutAName_HasAnEmptyName()
    {
        var parsed = QueryParser.Parse("500 mg two strips please");
        Assert.Equal(string.Empty, parsed.NameText);
        Assert.Equal([Mg(500)], parsed.Strengths);
    }
}

public sealed class StrengthParserTests
{
    [Theory]
    [InlineData("500 mg", new[] { 500.0 }, new[] { StrengthUnit.Milligram })]
    [InlineData("500mg", new[] { 500.0 }, new[] { StrengthUnit.Milligram })]
    [InlineData("120 mg/5 ml", new[] { 120.0, 5.0 }, new[] { StrengthUnit.Milligram, StrengthUnit.Millilitre })]
    [InlineData("500 mg + 65 mg", new[] { 500.0, 65.0 }, new[] { StrengthUnit.Milligram, StrengthUnit.Milligram })]
    [InlineData("500 mg + 200 IU", new[] { 500.0, 200.0 }, new[] { StrengthUnit.Milligram, StrengthUnit.InternationalUnit })]
    [InlineData("100 mcg/puff", new[] { 100.0 }, new[] { StrengthUnit.Microgram })]
    [InlineData("1 g", new[] { 1000.0 }, new[] { StrengthUnit.Milligram })]
    [InlineData("0.05%", new[] { 0.05 }, new[] { StrengthUnit.Percent })]
    [InlineData("80 mg/ml", new[] { 80.0 }, new[] { StrengthUnit.Milligram })]
    public void CatalogStrengths_AreParsedIntoComponents(string text, double[] amounts, StrengthUnit[] units)
    {
        var expected = amounts.Zip(units, (a, u) => new StrengthValue((decimal)a, u)).ToList();
        Assert.Equal(expected, StrengthParser.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("as directed")]
    public void MissingOrUnparsableStrengths_GiveNoComponents(string? text)
    {
        Assert.Empty(StrengthParser.Parse(text));
    }

    [Fact]
    public void StrengthValue_FormatsWithItsUnit()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture; // display text uses the current culture
        try
        {
            Assert.Equal("500 mg", new StrengthValue(500, StrengthUnit.Milligram).ToString());
            Assert.Equal("0.5 ml", new StrengthValue(0.5m, StrengthUnit.Millilitre).ToString());
            Assert.Equal("0.1%", new StrengthValue(0.1m, StrengthUnit.Percent).ToString());
            Assert.Equal("200 IU", new StrengthValue(200, StrengthUnit.InternationalUnit).ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
