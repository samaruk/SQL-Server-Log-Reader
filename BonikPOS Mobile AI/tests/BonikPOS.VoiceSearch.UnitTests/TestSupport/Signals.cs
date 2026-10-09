namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>Deterministic synthetic signals.</summary>
public static class Signals
{
    public static float[] Sine(int sampleRate, double frequency, double seconds, double amplitude = 0.5, double phase = 0)
    {
        var s = new float[(int)Math.Round(sampleRate * seconds)];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate + phase));
        }

        return s;
    }

    /// <summary>Uniform noise in [-amplitude, amplitude] from a fixed seed.</summary>
    public static float[] Noise(int length, int seed, double amplitude = 0.5)
    {
        var rng = new Random(seed);
        var s = new float[length];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (float)((rng.NextDouble() * 2 - 1) * amplitude);
        }

        return s;
    }

    /// <summary>0, 1, 2, ... scaled so every sample value is unique and exactly representable.</summary>
    public static float[] Ramp(int length, float scale = 1f / 65536)
    {
        var s = new float[length];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = i * scale;
        }

        return s;
    }

    /// <summary>Splits <paramref name="total"/> into pseudo-random block sizes in [1, maxBlock] (fixed seed).</summary>
    public static List<int> RandomBlocks(int total, int maxBlock, int seed)
    {
        var rng = new Random(seed);
        var blocks = new List<int>();
        int left = total;
        while (left > 0)
        {
            int n = Math.Min(left, rng.Next(1, maxBlock + 1));
            blocks.Add(n);
            left -= n;
        }

        return blocks;
    }

    public static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (float v in s)
        {
            sum += (double)v * v;
        }

        return s.IsEmpty ? 0 : Math.Sqrt(sum / s.Length);
    }
}
