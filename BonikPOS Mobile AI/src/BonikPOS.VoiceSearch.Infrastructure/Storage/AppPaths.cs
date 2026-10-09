namespace BonikPOS.VoiceSearch.Infrastructure.Storage;

/// <summary>App directories. On devices these come from MAUI FileSystem (private app storage).</summary>
public interface IAppPaths
{
    /// <summary>Private, backed-up-excluded data directory (profile, history, settings).</summary>
    string DataDirectory { get; }

    string CacheDirectory { get; }

    /// <summary>Where models bundled in the app package are extracted.</summary>
    string BundledModelsDirectory { get; }

    /// <summary>Where developers can side-load models (Android: app external files dir; iOS: Documents, via Files app).</summary>
    string SideloadModelsDirectory { get; }
}

public sealed record AppPaths(string DataDirectory, string CacheDirectory, string BundledModelsDirectory, string SideloadModelsDirectory) : IAppPaths;

/// <summary>Writes files atomically (temp file + move) so a crash never leaves a half-written profile.</summary>
public static class AtomicFile
{
    public static async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
