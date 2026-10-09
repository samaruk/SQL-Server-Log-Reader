using System.Globalization;
using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Models;

namespace BonikPOS.VoiceSearch.AI.Onnx;

/// <summary>Typed access to the adapter options stored in a manifest entry.</summary>
public sealed class ModelOptions(ModelDescriptor descriptor)
{
    private readonly JsonElement? _options = descriptor.Options;

    public string String(string name, string fallback) =>
        TryGet(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? fallback : fallback;

    public string? OptionalString(string name) =>
        TryGet(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public int Int(string name, int fallback) =>
        TryGet(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : fallback;

    public double Double(string name, double fallback) =>
        TryGet(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : fallback;

    public bool Bool(string name, bool fallback) =>
        TryGet(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : fallback;

    public int RequiredInt(string name) =>
        TryGet(name, out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32()
            : throw new ModelContractException($"Manifest entry '{descriptor.Id}' must define integer option '{name}'.");

    public string RequiredString(string name) =>
        OptionalString(name) ?? throw new ModelContractException($"Manifest entry '{descriptor.Id}' must define option '{name}'.");

    public IReadOnlyList<JsonElement> Array(string name) =>
        TryGet(name, out var e) && e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray()] : [];

    public ModelOptions Section(string name)
    {
        if (TryGet(name, out var e) && e.ValueKind == JsonValueKind.Object)
        {
            return new ModelOptions(descriptor with { Options = e });
        }

        return new ModelOptions(descriptor with { Options = null });
    }

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return _options is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out value);
    }

    public override string ToString() => _options?.GetRawText() ?? "{}";

    public static string Invariant(double v) => v.ToString(CultureInfo.InvariantCulture);
}
