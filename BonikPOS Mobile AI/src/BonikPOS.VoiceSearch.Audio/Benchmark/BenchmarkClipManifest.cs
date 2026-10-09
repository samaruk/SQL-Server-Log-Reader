using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Benchmark;

namespace BonikPOS.VoiceSearch.Audio.Benchmark;

/// <summary>
/// Reads benchmark clips listed in a manifest (16 kHz WAV files next to it):
/// <code>
/// { "format": "bonikpos-benchmark-clips/1",
///   "clips": [ { "file": "owner-1.wav", "owner": true, "enroll": true }, { "file": "other.wav", "owner": false } ] }
/// </code>
/// "enroll" clips build the benchmark's temporary owner reference; the others are replayed through the pipeline.
/// </summary>
public static class BenchmarkClipManifest
{
    public const string Format = "bonikpos-benchmark-clips/1";

    public static async Task<IReadOnlyList<BenchmarkClip>> ReadDirectoryAsync(string directory, string manifestFile, CancellationToken cancellationToken)
    {
        await using var manifest = File.OpenRead(Path.Combine(directory, manifestFile));
        return await ReadAsync(manifest, file => Task.FromResult<Stream>(File.OpenRead(Path.Combine(directory, file))), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <param name="open">Opens a clip by its plain file name.</param>
    public static async Task<IReadOnlyList<BenchmarkClip>> ReadAsync(Stream manifest, Func<string, Task<Stream>> open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        using var doc = await JsonDocument.ParseAsync(manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("format", out var format) || format.GetString() != Format)
        {
            throw new InvalidDataException($"The clip manifest must declare \"format\": \"{Format}\".");
        }

        var clips = new List<BenchmarkClip>();
        foreach (var entry in doc.RootElement.GetProperty("clips").EnumerateArray())
        {
            string file = entry.GetProperty("file").GetString() ?? throw new InvalidDataException("A clip has no file name.");
            if (file.Length == 0 || file.Contains('/', StringComparison.Ordinal) || file.Contains('\\', StringComparison.Ordinal) || file.StartsWith('.'))
            {
                throw new InvalidDataException($"Clip file '{file}' must be a plain file name next to the manifest.");
            }

            var stream = await open(file).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                buffer.Position = 0;
                var pcm = WavFile.Read(buffer);
                if (pcm.SampleRate != 16000)
                {
                    throw new InvalidDataException($"Clip '{file}' is {pcm.SampleRate} Hz; benchmark clips must be 16 kHz.");
                }

                clips.Add(new BenchmarkClip(file, pcm.ToMono(),
                    entry.TryGetProperty("owner", out var owner) && owner.GetBoolean(),
                    entry.TryGetProperty("enroll", out var enroll) && enroll.GetBoolean()));
            }
        }

        return clips;
    }
}
