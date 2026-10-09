using BonikPOS.VoiceSearch.Core.Vad;

namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>
/// VAD fake: returns a scripted probability per chunk (0 once the script is exhausted) and records every chunk it saw.
/// </summary>
public sealed class ScriptedVad(IReadOnlyList<float> probabilities, int chunkSamples = 160, int sampleRate = 16000) : IVoiceActivityDetector
{
    private int _next;

    public string ModelId => "scripted-vad";

    public int SampleRate { get; } = sampleRate;

    public int ChunkSamples { get; } = chunkSamples;

    public int ResetCount { get; private set; }

    public List<float[]> Chunks { get; } = [];

    public bool Disposed { get; private set; }

    public float Process(ReadOnlySpan<float> chunk)
    {
        Assert.Equal(ChunkSamples, chunk.Length);
        Chunks.Add(chunk.ToArray());
        return _next < probabilities.Count ? probabilities[_next++] : 0f;
    }

    public void Reset() => ResetCount++;

    public void Dispose() => Disposed = true;

    /// <summary>"0.1x10, 0.9x20" -> ten 0.1s followed by twenty 0.9s.</summary>
    public static float[] Script(params (float P, int Count)[] runs) =>
        [.. runs.SelectMany(r => Enumerable.Repeat(r.P, r.Count))];
}

/// <summary>Speech probability from chunk energy: loud chunks are "speech". Lets synthetic audio drive the pipeline.</summary>
public sealed class EnergyVad(int chunkSamples = 512, double rmsThreshold = 0.02) : IVoiceActivityDetector
{
    public string ModelId => "energy-vad";

    public int SampleRate => 16000;

    public int ChunkSamples { get; } = chunkSamples;

    public int ResetCount { get; private set; }

    public bool Disposed { get; private set; }

    public float Process(ReadOnlySpan<float> chunk) => Signals.Rms(chunk) > rmsThreshold ? 0.95f : 0.02f;

    public void Reset() => ResetCount++;

    public void Dispose() => Disposed = true;
}
