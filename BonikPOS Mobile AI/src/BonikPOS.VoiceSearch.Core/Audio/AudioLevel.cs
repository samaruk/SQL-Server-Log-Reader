namespace BonikPOS.VoiceSearch.Core.Audio;

/// <summary>Level measurements on float PCM in [-1, 1].</summary>
public static class AudioLevel
{
    /// <summary>Floor used for silence, so that logarithms stay finite.</summary>
    public const double SilenceDbfs = -120.0;

    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (float s in samples)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    public static double Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (float s in samples)
        {
            float a = Math.Abs(s);
            if (a > peak)
            {
                peak = a;
            }
        }

        return peak;
    }

    public static double ToDbfs(double linear) => linear <= 1e-6 ? SilenceDbfs : 20.0 * Math.Log10(linear);

    public static double RmsDbfs(ReadOnlySpan<float> samples) => ToDbfs(Rms(samples));

    /// <summary>Fraction of samples at or above <paramref name="threshold"/> in magnitude (default: within 0.1 dB of full scale).</summary>
    public static double ClippingRatio(ReadOnlySpan<float> samples, float threshold = 0.989f)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        int n = 0;
        foreach (float s in samples)
        {
            if (Math.Abs(s) >= threshold)
            {
                n++;
            }
        }

        return (double)n / samples.Length;
    }
}
