using System.Globalization;
using System.Text.RegularExpressions;

namespace BonikPOS.VoiceSearch.App.Converters;

public sealed class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class HasTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"AutoBanglaEnglish" -> "Auto Bangla English" for pickers bound to enum values.</summary>
public sealed partial class EnumDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? null : SplitWords().Replace(value.ToString() ?? string.Empty, " $1");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    [GeneratedRegex("(?<=[a-z])([A-Z])")]
    private static partial Regex SplitWords();
}
