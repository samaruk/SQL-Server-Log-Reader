using System.Security.Cryptography;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Infrastructure.Models;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Infrastructure;

public sealed class FileSystemModelProviderTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private static readonly CancellationToken Ct = CancellationToken.None;

    public void Dispose() => _dir.Dispose();

    private string Root(string name)
    {
        string root = Path.Combine(_dir.Path, name);
        Directory.CreateDirectory(Path.Combine(root, "vad"));
        return root;
    }

    private static string WriteModel(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void WriteManifest(string root, string entries) =>
        File.WriteAllText(Path.Combine(root, "models.json"), $$"""{"format": "bonikpos-models/1", "models": [{{entries}}]}""");

    private static string Entry(string id, string file, string? sha, string role = "VoiceActivity") =>
        $$"""{"id": "{{id}}", "version": "1", "role": "{{role}}", "format": "silero-vad", "files": {"model": "{{file}}"}, "license": "MIT", "source": "unit test"{{(sha is null ? "" : $$""", "sha256": {"{{file}}": "{{sha}}"}""")}}}""";

    [Fact]
    public async Task VerifiedModel_IsResolvedWithAbsolutePaths()
    {
        string root = Root("bundled");
        string sha = WriteModel(root, "vad/model.onnx", "weights");
        WriteManifest(root, Entry("vad-a", "vad/model.onnx", sha.ToUpperInvariant())); // checksums compare case-insensitively
        var provider = new FileSystemModelProvider([root], checksumCachePath: null);

        var model = await provider.ResolveAsync(ModelRole.VoiceActivity, Ct);
        var status = (await provider.GetStatusAsync(Ct)).Single(s => s.Role == ModelRole.VoiceActivity);

        Assert.NotNull(model);
        Assert.Equal(Path.Combine(root, "vad", "model.onnx"), model!.PathOf("model"));
        Assert.Equal("vad-a", model.Descriptor.Id);
        Assert.Equal(ModelState.Ready, status.State);
        Assert.Equal("Ready (checksums verified).", status.Message);
        Assert.Equal(sha, Assert.Single(status.Files).ActualSha256);
        Assert.Equal(ModelState.NotInstalled, (await provider.GetStatusAsync(Ct)).Single(s => s.Role == ModelRole.SpeechRecognition).State);
    }

    [Fact]
    public async Task ModifiedFile_IsNeverHandedToAnAdapter()
    {
        string root = Root("bundled");
        WriteModel(root, "vad/model.onnx", "weights");
        WriteManifest(root, Entry("vad-a", "vad/model.onnx", new string('0', 64)));
        var provider = new FileSystemModelProvider([root], null);

        Assert.Null(await provider.ResolveAsync(ModelRole.VoiceActivity, Ct));
        var status = (await provider.GetStatusAsync(Ct)).Single(s => s.Role == ModelRole.VoiceActivity);
        Assert.Equal(ModelState.ChecksumMismatch, status.State);
        Assert.Contains("SHA-256 mismatch", status.Message);
    }

    [Fact]
    public async Task ManifestWithoutChecksums_IsAccepted_AndSaysSo()
    {
        string root = Root("sideload");
        WriteModel(root, "vad/model.onnx", "weights");
        File.WriteAllText(Path.Combine(root, "models.json"), """
            {"format": "bonikpos-models/1", "models": [{"id": "vad-dev", "version": "1", "role": "VoiceActivity",
              "format": "silero-vad", "files": {"model": "vad/model.onnx"}}]}
            """);
        var provider = new FileSystemModelProvider([root], null);

        var model = await provider.ResolveAsync(ModelRole.VoiceActivity, Ct);
        var status = (await provider.GetStatusAsync(Ct)).Single(s => s.Role == ModelRole.VoiceActivity);

        Assert.NotNull(model);
        Assert.Equal(ModelState.Ready, status.State);
        Assert.Contains("no checksum pinned", status.Message);
        Assert.NotNull(model!.Descriptor.Sha256);
        Assert.Equal("unknown", model.Descriptor.License);
        Assert.Equal("unknown", model.Descriptor.Source);
        Assert.False(model.Descriptor.IsTestFixture);
    }

    [Theory]
    [InlineData("../outside.onnx")]
    [InlineData("../models-evil/model.onnx")] // a sibling directory whose name starts with the root's name
    public async Task FilesOutsideTheModelDirectory_AreRejected(string relative)
    {
        string root = Root("models");
        WriteModel(root, relative, "weights");
        WriteManifest(root, Entry("vad-escape", relative, sha: null));
        var provider = new FileSystemModelProvider([root], null);

        Assert.Null(await provider.ResolveAsync(ModelRole.VoiceActivity, Ct));
        var status = (await provider.GetStatusAsync(Ct)).Single(s => s.Role == ModelRole.VoiceActivity);
        Assert.Equal(ModelState.InvalidManifest, status.State);
        Assert.Contains("points outside the model directory", status.Message);
    }

    [Fact]
    public async Task SideloadedModels_TakePriority_AndBrokenEntriesFallBackToTheNextRoot()
    {
        string sideload = Root("sideload");
        string bundled = Root("bundled");
        string sha = WriteModel(bundled, "vad/model.onnx", "bundled weights");
        WriteManifest(bundled, Entry("vad-bundled", "vad/model.onnx", sha));
        WriteManifest(sideload, Entry("vad-missing", "vad/not-there.onnx", "abc"));
        var provider = new FileSystemModelProvider([sideload, bundled], null);

        Assert.Equal("vad-bundled", (await provider.ResolveAsync(ModelRole.VoiceActivity, Ct))!.Descriptor.Id);

        string sideSha = WriteModel(sideload, "vad/not-there.onnx", "sideloaded weights");
        WriteManifest(sideload, Entry("vad-missing", "vad/not-there.onnx", sideSha));
        Assert.Equal("vad-missing", (await provider.ResolveAsync(ModelRole.VoiceActivity, Ct))!.Descriptor.Id);
    }

    [Fact]
    public async Task InvalidOrForeignManifests_AreIgnored()
    {
        string a = Root("a");
        string b = Root("b");
        File.WriteAllText(Path.Combine(a, "models.json"), "{ not json");
        File.WriteAllText(Path.Combine(b, "models.json"), """{"format": "someone-else/9", "models": []}""");
        var provider = new FileSystemModelProvider([a, b, Path.Combine(_dir.Path, "missing-root")], null);

        Assert.Empty(provider.ReadManifests());
        Assert.Null(await provider.ResolveAsync(ModelRole.VoiceActivity, Ct));
    }

    [Fact]
    public async Task ReportedAdapterFailures_AppearInTheStatus()
    {
        string root = Root("bundled");
        string sha = WriteModel(root, "vad/model.onnx", "weights");
        WriteManifest(root, Entry("vad-a", "vad/model.onnx", sha));
        var provider = new FileSystemModelProvider([root], null);

        provider.ReportFailure(ModelRole.VoiceActivity, ModelState.IncompatibleContract, "input 'input' must be a Float tensor");

        var status = (await provider.GetStatusAsync(Ct)).Single(s => s.Role == ModelRole.VoiceActivity);
        Assert.Equal(ModelState.IncompatibleContract, status.State);
        Assert.Contains("Float tensor", status.Message);
    }

    [Fact]
    public async Task ChecksumCache_IsWrittenAndReused_UntilTheFileChanges()
    {
        string root = Root("bundled");
        string sha = WriteModel(root, "vad/model.onnx", "weights");
        WriteManifest(root, Entry("vad-a", "vad/model.onnx", sha));
        string cache = Path.Combine(_dir.Path, "cache", "checksums.json");

        Assert.NotNull(await new FileSystemModelProvider([root], cache).ResolveAsync(ModelRole.VoiceActivity, Ct));
        Assert.True(File.Exists(cache));
        Assert.Contains(sha, File.ReadAllText(cache));

        // Same size and timestamp as cached: the cached hash is trusted. Different content changes the timestamp/size.
        File.WriteAllText(Path.Combine(root, "vad", "model.onnx"), "tampered weights!");
        Assert.Null(await new FileSystemModelProvider([root], cache).ResolveAsync(ModelRole.VoiceActivity, Ct));
    }
}
