using BonikPOS.VoiceSearch.Core.Recognition;

namespace BonikPOS.VoiceSearch.AI.Recognition;

/// <summary>Reports that no speech-recognition model is installed. The app then offers typed search only.</summary>
public sealed class UnavailableSpeechRecognizer(string message) : ISpeechRecognitionService
{
    public SpeechRecognizerInfo Info { get; } = new(false, null, "No speech-recognition model installed", 0, message);

    public Task<SpeechRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<float> audio16k, SpeechRecognitionOptions options, CancellationToken cancellationToken) =>
        throw new SpeechRecognitionUnavailableException(message);

    public void Dispose()
    {
    }
}
