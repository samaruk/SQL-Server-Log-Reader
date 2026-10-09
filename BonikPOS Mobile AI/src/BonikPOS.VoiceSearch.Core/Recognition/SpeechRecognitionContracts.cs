namespace BonikPOS.VoiceSearch.Core.Recognition;

public enum RecognitionLanguageMode
{
    /// <summary>Detect the language per utterance, restricted to Bangla or English.</summary>
    AutoBanglaEnglish,
    Bangla,
    English,
}

public sealed record SpeechRecognitionOptions
{
    public RecognitionLanguageMode LanguageMode { get; init; } = RecognitionLanguageMode.AutoBanglaEnglish;

    /// <summary>Upper bound of generated tokens. Bangla needs about two tokens per character with Whisper's vocabulary.</summary>
    public int MaxTokens { get; init; } = 128;
}

public sealed record SpeechRecognitionResult(
    string Text,
    string Language,
    double Confidence,
    double AverageLogProbability,
    double NoSpeechProbability,
    int TokenCount,
    TimeSpan AudioDuration,
    TimeSpan FeatureTime,
    TimeSpan EncoderTime,
    TimeSpan DecoderTime,
    string ModelId,
    bool Truncated);

public sealed record SpeechRecognizerInfo(
    bool IsAvailable,
    string? ModelId,
    string Description,
    int MaxAudioSamples,
    string StatusMessage);

/// <summary>Speech-to-text for the owner's (already extracted / verified) utterance.</summary>
public interface ISpeechRecognitionService : IDisposable
{
    SpeechRecognizerInfo Info { get; }

    /// <summary>Recognises one utterance of 16 kHz mono audio. CPU heavy: runs on a background thread.</summary>
    /// <exception cref="SpeechRecognitionUnavailableException">No recognition model is installed.</exception>
    Task<SpeechRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<float> audio16k, SpeechRecognitionOptions options, CancellationToken cancellationToken);
}

public sealed class SpeechRecognitionUnavailableException(string message) : InvalidOperationException(message);
