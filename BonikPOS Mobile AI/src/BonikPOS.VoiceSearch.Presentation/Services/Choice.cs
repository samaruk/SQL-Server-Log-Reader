namespace BonikPOS.VoiceSearch.Presentation.Services;

/// <summary>A picker entry: a value with a readable label (ToString is what pickers display).</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public static class Choices
{
    public static IReadOnlyList<Choice<T>> Of<T>(params (T Value, string Label)[] items) => [.. items.Select(i => new Choice<T>(i.Value, i.Label))];

    public static Choice<T> Find<T>(IReadOnlyList<Choice<T>> choices, T value) =>
        choices.FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value)) ?? choices[0];
}
