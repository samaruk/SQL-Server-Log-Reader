using BonikPOS.VoiceSearch.Infrastructure.Storage;

namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>A unique directory under the system temp folder, deleted (recursively) on dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bonikpos-unit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>App paths rooted in this directory (data, cache and model folders are created on demand).</summary>
    public AppPaths AppPaths() => new(
        System.IO.Path.Combine(Path, "data"),
        System.IO.Path.Combine(Path, "cache"),
        System.IO.Path.Combine(Path, "models-bundled"),
        System.IO.Path.Combine(Path, "models-sideload"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a file still mapped by a native library must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
