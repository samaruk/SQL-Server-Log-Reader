using System.Buffers.Binary;
using System.Text.Json;
using BonikPOS.VoiceSearch.Audio;

namespace BonikPOS.VoiceSearch.IntegrationTests.TestSupport;

/// <summary>
/// Locates the solution folder (the one containing models/models.json) by walking up from the test binary, or from
/// the BONIKPOS_SOLUTION_ROOT environment variable when set.
/// </summary>
public static class SolutionPaths
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    public static string Root => RootPath.Value;

    /// <summary>The bundled models folder (has models.json).</summary>
    public static string Models => Path.Combine(Root, "models");

    public static string Fixtures => Path.Combine(Root, "tests", "fixtures");

    public static string AudioFile(string name) => Path.Combine(Fixtures, "audio", name);

    public static string ReferenceFile(string name) => Path.Combine(Fixtures, "reference", name);

    public static string WhisperFixture(string name) => Path.Combine(Fixtures, "whisper-test-tiny", name);

    /// <summary>A 16 kHz mono fixture clip as float samples (int16 / 32768, like soundfile's float32 reading).</summary>
    public static float[] ReadClip(string name)
    {
        var audio = WavFile.Read(AudioFile(name));
        Assert.Equal(16000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
        return audio.Samples;
    }

    public static PcmAudio ReadPcm(string name) => WavFile.Read(AudioFile(name));

    /// <summary>Little-endian float32 array written by numpy's tofile.</summary>
    public static float[] ReadF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var values = new float[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        }

        return values;
    }

    public static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

    private static string FindRoot()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("BONIKPOS_SOLUTION_ROOT");
        if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(Path.Combine(fromEnvironment, "models", "models.json")))
        {
            return fromEnvironment;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "models", "models.json"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find the solution root (a folder with models/models.json and tests/fixtures) above {AppContext.BaseDirectory}. Set BONIKPOS_SOLUTION_ROOT.");
    }
}

/// <summary>A unique temporary directory, deleted on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bonikpos-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Error statistics for parity checks, reported in the test output.</summary>
public static class Errors
{
    public static double MaxAbs(ReadOnlySpan<float> actual, ReadOnlySpan<float> expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        double max = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            max = Math.Max(max, Math.Abs((double)actual[i] - expected[i]));
        }

        return max;
    }

    public static double MaxAbs(IReadOnlyList<double> actual, IReadOnlyList<double> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        double max = 0;
        for (int i = 0; i < actual.Count; i++)
        {
            max = Math.Max(max, Math.Abs(actual[i] - expected[i]));
        }

        return max;
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return dot / Math.Sqrt(na * nb);
    }
}
