using System.Security.Cryptography;
using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BonikPOS.VoiceSearch.Infrastructure.Models;

/// <summary>
/// Finds models through models.json manifests in one or more roots (side-loaded first, then bundled) and verifies
/// every file against the SHA-256 in its manifest. Hashes are cached by path, size and modification time so large
/// models are hashed once. A file whose hash does not match is never handed to an adapter.
/// </summary>
public sealed class FileSystemModelProvider : IModelProvider
{
    public const string ManifestFileName = "models.json";
    public const string ManifestFormat = "bonikpos-models/1";

    private readonly IReadOnlyList<string> _roots;
    private readonly string? _checksumCachePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ModelRole, (ModelState State, string Message)> _failures = [];
    private Dictionary<string, ChecksumCacheEntry>? _checksums;

    /// <param name="roots">Directories that may contain models.json, in priority order.</param>
    public FileSystemModelProvider(IReadOnlyList<string> roots, string? checksumCachePath, ILogger<FileSystemModelProvider>? logger = null)
    {
        _roots = roots;
        _checksumCachePath = checksumCachePath;
        _logger = logger ?? NullLogger<FileSystemModelProvider>.Instance;
    }

    public async Task<IReadOnlyList<ModelStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var result = new List<ModelStatus>();
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var evaluated = await EvaluateRoleAsync(role, cancellationToken).ConfigureAwait(false);
            var chosen = evaluated.FirstOrDefault(e => e.Status.State == ModelState.Ready) ?? evaluated.FirstOrDefault();
            var status = chosen?.Status ?? new ModelStatus(role, null, null, ModelState.NotInstalled, "No model is installed for this role.", [], null, null);
            if (status.State == ModelState.Ready && _failures.TryGetValue(role, out var failure))
            {
                status = status with { State = failure.State, Message = failure.Message };
            }

            result.Add(status);
        }

        return result;
    }

    public async Task<ResolvedModel?> ResolveAsync(ModelRole role, CancellationToken cancellationToken)
    {
        var evaluated = await EvaluateRoleAsync(role, cancellationToken).ConfigureAwait(false);
        return evaluated.FirstOrDefault(e => e.Status.State == ModelState.Ready)?.Resolved;
    }

    public void ReportFailure(ModelRole role, ModelState state, string message)
    {
        _failures[role] = (state, message);
        _logger.LogWarning("Model for {Role} reported {State}: {Message}", role, state, message);
    }

    /// <summary>
    /// The source-generated serializer sets init-only properties that are absent from the JSON to null instead of their
    /// declared defaults. Checksums, licence and source are optional in a manifest, so their defaults are restored here.
    /// </summary>
    internal static ModelDescriptor WithOptionalDefaults(ModelDescriptor d) => d with
    {
        Sha256 = d.Sha256 ?? new Dictionary<string, string>(),
        License = d.License ?? "unknown",
        Source = d.Source ?? "unknown",
    };

    /// <summary>Reads every manifest entry from all roots.</summary>
    public IReadOnlyList<(ModelDescriptor Descriptor, string Root)> ReadManifests()
    {
        var list = new List<(ModelDescriptor, string)>();
        foreach (var root in _roots)
        {
            string manifest = Path.Combine(root, ManifestFileName);
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                var file = JsonSerializer.Deserialize(File.ReadAllBytes(manifest), InfrastructureJsonContext.Default.ModelManifestFile);
                if (file is null || file.Format != ManifestFormat)
                {
                    _logger.LogWarning("Ignoring {Manifest}: unknown format", manifest);
                    continue;
                }

                list.AddRange((file.Models ?? []).Select(m => (WithOptionalDefaults(m), root)));
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Ignoring invalid manifest {Manifest}", manifest);
            }
        }

        return list;
    }

    private async Task<List<Evaluated>> EvaluateRoleAsync(ModelRole role, CancellationToken cancellationToken)
    {
        var result = new List<Evaluated>();
        foreach (var (descriptor, root) in ReadManifests().Where(m => m.Descriptor.Role == role))
        {
            result.Add(await EvaluateAsync(descriptor, root, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    private async Task<Evaluated> EvaluateAsync(ModelDescriptor d, string root, CancellationToken cancellationToken)
    {
        var files = new List<ModelFileStatus>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var state = ModelState.Ready;
        var messages = new List<string>();
        // With a trailing separator, so that a sibling such as "models-evil" does not count as inside "models".
        string rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        foreach (var (logical, relative) in d.Files)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                state = ModelState.InvalidManifest;
                messages.Add($"'{relative}' points outside the model directory.");
                continue;
            }

            if (!File.Exists(full))
            {
                files.Add(new ModelFileStatus(logical, relative, false, null, d.Sha256.GetValueOrDefault(relative), null));
                if (state == ModelState.Ready)
                {
                    state = ModelState.NotInstalled;
                }

                messages.Add($"Missing {relative}.");
                continue;
            }

            string? expected = d.Sha256.GetValueOrDefault(relative);
            string actual = await HashAsync(full, cancellationToken).ConfigureAwait(false);
            long bytes = new FileInfo(full).Length;
            files.Add(new ModelFileStatus(logical, relative, true, bytes, expected, actual));
            if (expected is null)
            {
                messages.Add($"{relative}: no checksum pinned in the manifest.");
            }
            else if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                state = ModelState.ChecksumMismatch;
                messages.Add($"{relative}: SHA-256 mismatch.");
            }

            paths[logical] = full;
        }

        string message = state == ModelState.Ready
            ? (messages.Count == 0 ? "Ready (checksums verified)." : "Ready. " + string.Join(" ", messages))
            : string.Join(" ", messages);
        var status = new ModelStatus(d.Role, d.Id, d.Version, state, message, files, d.License, d.Source);
        return new Evaluated(status, state == ModelState.Ready ? new ResolvedModel(d, paths) : null);
    }

    private async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _checksums ??= LoadChecksumCache();
            if (_checksums.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            {
                return cached.Sha256;
            }

            string hash;
            await using (var stream = File.OpenRead(path))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            _checksums[path] = new ChecksumCacheEntry(path, info.Length, info.LastWriteTimeUtc.Ticks, hash);
            SaveChecksumCache();
            return hash;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, ChecksumCacheEntry> LoadChecksumCache()
    {
        if (_checksumCachePath is null || !File.Exists(_checksumCachePath))
        {
            return new Dictionary<string, ChecksumCacheEntry>(StringComparer.Ordinal);
        }

        try
        {
            var list = JsonSerializer.Deserialize(File.ReadAllBytes(_checksumCachePath), InfrastructureJsonContext.Default.ListChecksumCacheEntry) ?? [];
            return list.ToDictionary(e => e.Path, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, ChecksumCacheEntry>(StringComparer.Ordinal);
        }
    }

    private void SaveChecksumCache()
    {
        if (_checksumCachePath is null || _checksums is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_checksumCachePath)!);
            File.WriteAllBytes(_checksumCachePath, JsonSerializer.SerializeToUtf8Bytes(_checksums.Values.ToList(), InfrastructureJsonContext.Default.ListChecksumCacheEntry));
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Checksum cache not written");
        }
    }

    private sealed record Evaluated(ModelStatus Status, ResolvedModel? Resolved);
}

/// <summary>Read access to files packaged inside the app (MAUI raw assets on device).</summary>
public interface IAppPackageFiles
{
    Task<Stream> OpenAsync(string relativePath);

    Task<bool> ExistsAsync(string relativePath);
}

/// <summary>
/// Copies models packaged with the app (Resources/Raw/models) into private storage, because ONNX Runtime loads
/// models from file paths. Each file is verified against the bundled manifest while it is copied.
/// </summary>
public static class BundledModelExtractor
{
    public static async Task<int> ExtractAsync(IAppPackageFiles package, string packageFolder, string targetDirectory, CancellationToken cancellationToken)
    {
        string manifestPath = $"{packageFolder}/{FileSystemModelProvider.ManifestFileName}";
        if (!await package.ExistsAsync(manifestPath).ConfigureAwait(false))
        {
            return 0;
        }

        Directory.CreateDirectory(targetDirectory);
        byte[] manifestBytes;
        await using (var s = await package.OpenAsync(manifestPath).ConfigureAwait(false))
        using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            manifestBytes = ms.ToArray();
        }

        var manifest = JsonSerializer.Deserialize(manifestBytes, InfrastructureJsonContext.Default.ModelManifestFile)
                       ?? throw new InvalidDataException("Bundled model manifest is empty.");
        int copied = 0;
        foreach (var model in (manifest.Models ?? []).Where(m => !m.IsTestFixture).Select(FileSystemModelProvider.WithOptionalDefaults))
        {
            foreach (var relative in model.Files.Values)
            {
                string packagePath = $"{packageFolder}/{relative}";
                if (!await package.ExistsAsync(packagePath).ConfigureAwait(false))
                {
                    continue; // optional model not bundled in this build
                }

                string target = Path.Combine(targetDirectory, relative);
                string? expected = model.Sha256.GetValueOrDefault(relative);
                if (File.Exists(target) && expected is not null && await IsHashAsync(target, expected, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temp = target + ".tmp";
                await using (var source = await package.OpenAsync(packagePath).ConfigureAwait(false))
                await using (var dest = File.Create(temp))
                {
                    await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
                }

                if (expected is not null && !await IsHashAsync(temp, expected, cancellationToken).ConfigureAwait(false))
                {
                    File.Delete(temp);
                    throw new InvalidDataException($"Bundled model file {relative} does not match its checksum.");
                }

                File.Move(temp, target, overwrite: true);
                copied++;
            }
        }

        await File.WriteAllBytesAsync(Path.Combine(targetDirectory, FileSystemModelProvider.ManifestFileName), manifestBytes, cancellationToken).ConfigureAwait(false);
        return copied;
    }

    private static async Task<bool> IsHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
    }
}
