using System.Text.Json;
using System.Text.Json.Serialization;
using BonikPOS.VoiceSearch.Core.Medicine;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Evaluation.Search;

/// <summary>
/// One labelled search case (format "bonikpos-search-eval/1", one JSON object per line). See docs/SEARCH_EVALUATION.md.
/// </summary>
public sealed record SearchEvalCase
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>The query text exactly as the recogniser (or the owner) produced it.</summary>
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    /// <summary>Category tags used for the per-category breakdown (e.g. "bn-script", "typo", "neg-unknown-brand").</summary>
    [JsonPropertyName("kind")]
    public IReadOnlyList<string> Kind { get; init; } = [];

    /// <summary>"en", "bn" or "banglish".</summary>
    [JsonPropertyName("lang")]
    public string? Lang { get; init; }

    [JsonPropertyName("expect")]
    public required SearchExpectation Expect { get; init; }

    /// <summary>"Voice" (default), "Typed" or "Correction".</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>Other recogniser hypotheses (n-best / other language).</summary>
    [JsonPropertyName("alternatives")]
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    [JsonPropertyName("recognition_confidence")]
    public double? RecognitionConfidence { get; init; }

    /// <summary>Owner-voice decision name (OwnerVoiceDecision), e.g. "Uncertain".</summary>
    [JsonPropertyName("speaker_decision")]
    public string? SpeakerDecision { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonIgnore]
    public bool IsNegative => Expect.Products.Count == 0;

    public MedicineQuery ToQuery() => new(
        Query,
        Source is null ? QuerySource.Voice : Enum.Parse<QuerySource>(Source, ignoreCase: true),
        RecognitionConfidence,
        SpeakerDecision is null ? null : Enum.Parse<OwnerVoiceDecision>(SpeakerDecision, ignoreCase: true))
    {
        Alternatives = Alternatives,
    };

    public static IReadOnlyList<SearchEvalCase> ReadJsonLines(string path)
    {
        var cases = new List<SearchEvalCase>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int line = 0;
        foreach (var text in File.ReadLines(path))
        {
            line++;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var c = JsonSerializer.Deserialize<SearchEvalCase>(text)
                    ?? throw new InvalidDataException($"{path}:{line}: empty case");
            if (!ids.Add(c.Id))
            {
                throw new InvalidDataException($"{path}:{line}: duplicate case id '{c.Id}'");
            }

            foreach (var d in c.Expect.Decisions)
            {
                if (!Enum.TryParse<SearchDecision>(d, out _))
                {
                    throw new InvalidDataException($"{path}:{line}: unknown decision '{d}'");
                }
            }

            cases.Add(c);
        }

        return cases;
    }
}

public sealed record SearchExpectation
{
    /// <summary>Acceptable product ids (any one counts as correct). Empty = nothing in the catalog satisfies the request.</summary>
    [JsonPropertyName("products")]
    public IReadOnlyList<string> Products { get; init; } = [];

    /// <summary>Optional acceptable decisions (SearchDecision names), e.g. ["Ambiguous"].</summary>
    [JsonPropertyName("decisions")]
    public IReadOnlyList<string> Decisions { get; init; } = [];
}
