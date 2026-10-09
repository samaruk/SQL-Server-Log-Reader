namespace BonikPOS.VoiceSearch.Core.Vad;

/// <summary>Streaming voice activity detector operating on fixed-size chunks.</summary>
public interface IVoiceActivityDetector : IDisposable
{
    string ModelId { get; }

    int SampleRate { get; }

    /// <summary>Exact number of samples <see cref="Process"/> expects.</summary>
    int ChunkSamples { get; }

    /// <summary>Speech probability in [0, 1] for one chunk. Stateful across calls.</summary>
    float Process(ReadOnlySpan<float> chunk);

    void Reset();
}
