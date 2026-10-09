using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Models;

namespace BonikPOS.VoiceSearch.AI.Recognition.Whisper;

/// <summary>The "bonikpos-whisper-onnx/1" config written by tools/models/export_whisper_onnx.py.</summary>
public sealed class WhisperModelConfig
{
    public const string FormatId = "bonikpos-whisper-onnx/1";

    public required string ModelId { get; init; }

    public required string License { get; init; }

    public required string Source { get; init; }

    public required string Quantization { get; init; }

    public int Mels { get; init; }

    public int Frames { get; init; }

    public int AudioContext { get; init; }

    public int TextLayers { get; init; }

    public int TextState { get; init; }

    public int TextContext { get; init; }

    public int Vocabulary { get; init; }

    public int Eot { get; init; }

    public int Sot { get; init; }

    public int NoSpeech { get; init; }

    public int NoTimestamps { get; init; }

    public int Transcribe { get; init; }

    public int TimestampBegin { get; init; }

    public required IReadOnlyDictionary<string, int> Languages { get; init; }

    public required IReadOnlyList<int> SuppressTokens { get; init; }

    public required IReadOnlyList<int> SuppressAtBegin { get; init; }

    public bool IsRandomTestModel => Source.StartsWith("RANDOM WEIGHTS", StringComparison.Ordinal);

    public static WhisperModelConfig Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        string format = r.GetProperty("format").GetString() ?? string.Empty;
        if (format != FormatId)
        {
            throw new ModelContractException($"Whisper config format '{format}' is not supported (expected {FormatId}).");
        }

        var dims = r.GetProperty("dims");
        var features = r.GetProperty("features");
        var special = r.GetProperty("special_tokens");
        if (features.GetProperty("sample_rate").GetInt32() != 16000 || features.GetProperty("n_fft").GetInt32() != 400
            || features.GetProperty("hop_length").GetInt32() != 160)
        {
            throw new ModelContractException("Whisper feature parameters differ from 16 kHz / n_fft 400 / hop 160.");
        }

        return new WhisperModelConfig
        {
            ModelId = r.GetProperty("model_id").GetString() ?? "whisper",
            License = r.GetProperty("license").GetString() ?? "unknown",
            Source = r.GetProperty("source").GetString() ?? "unknown",
            Quantization = r.GetProperty("quantization").GetString() ?? "none",
            Mels = features.GetProperty("n_mels").GetInt32(),
            Frames = features.GetProperty("n_frames").GetInt32(),
            AudioContext = dims.GetProperty("n_audio_ctx_exported").GetInt32(),
            TextLayers = dims.GetProperty("n_text_layer").GetInt32(),
            TextState = dims.GetProperty("n_text_state").GetInt32(),
            TextContext = dims.GetProperty("n_text_ctx").GetInt32(),
            Vocabulary = dims.GetProperty("n_vocab").GetInt32(),
            Eot = special.GetProperty("eot").GetInt32(),
            Sot = special.GetProperty("sot").GetInt32(),
            NoSpeech = special.GetProperty("no_speech").GetInt32(),
            NoTimestamps = special.GetProperty("no_timestamps").GetInt32(),
            Transcribe = special.GetProperty("transcribe").GetInt32(),
            TimestampBegin = special.GetProperty("timestamp_begin").GetInt32(),
            Languages = r.GetProperty("languages").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
            SuppressTokens = [.. r.GetProperty("suppress_tokens").EnumerateArray().Select(e => e.GetInt32())],
            SuppressAtBegin = [.. r.GetProperty("suppress_at_begin").EnumerateArray().Select(e => e.GetInt32())],
        };
    }
}

/// <summary>Token id to UTF-8 bytes (tiktoken byte-level BPE), from "&lt;id&gt; &lt;base64&gt;" lines.</summary>
public sealed class WhisperVocabulary
{
    private readonly byte[][] _bytes;

    private WhisperVocabulary(byte[][] bytes) => _bytes = bytes;

    public int Count => _bytes.Length;

    public static WhisperVocabulary Load(string path, int expectedCount)
    {
        var bytes = new byte[expectedCount][];
        int seen = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            int id = int.Parse(line.AsSpan(0, space), System.Globalization.CultureInfo.InvariantCulture);
            if ((uint)id >= (uint)expectedCount)
            {
                throw new ModelContractException($"Token id {id} is outside the vocabulary size {expectedCount}.");
            }

            bytes[id] = Convert.FromBase64String(line[(space + 1)..]);
            seen++;
        }

        if (seen != expectedCount)
        {
            throw new ModelContractException($"Token file has {seen} entries; the model vocabulary has {expectedCount}.");
        }

        return new WhisperVocabulary(bytes);
    }

    /// <summary>Concatenates the bytes of text tokens (ids below <paramref name="firstSpecial"/>) and decodes UTF-8.</summary>
    public string Decode(IReadOnlyList<int> tokens, int firstSpecial)
    {
        int length = 0;
        foreach (int t in tokens)
        {
            if (t < firstSpecial)
            {
                length += _bytes[t].Length;
            }
        }

        var buffer = new byte[length];
        int offset = 0;
        foreach (int t in tokens)
        {
            if (t < firstSpecial)
            {
                _bytes[t].CopyTo(buffer, offset);
                offset += _bytes[t].Length;
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer);
    }
}
