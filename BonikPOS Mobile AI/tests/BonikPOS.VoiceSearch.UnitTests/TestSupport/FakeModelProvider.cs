using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Models;

namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary><see cref="IModelProvider"/> fake: returns the models a test registered and records reported failures.</summary>
public sealed class FakeModelProvider : IModelProvider
{
    private readonly Dictionary<ModelRole, ResolvedModel> _models = [];

    public List<(ModelRole Role, ModelState State, string Message)> Failures { get; } = [];

    public FakeModelProvider With(ModelRole role, ResolvedModel model)
    {
        _models[role] = model;
        return this;
    }

    public Task<IReadOnlyList<ModelStatus>> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ModelStatus>>([]);

    public Task<ResolvedModel?> ResolveAsync(ModelRole role, CancellationToken cancellationToken) =>
        Task.FromResult(_models.GetValueOrDefault(role));

    public void ReportFailure(ModelRole role, ModelState state, string message) => Failures.Add((role, state, message));

    /// <summary>A resolved model whose logical files map to the given paths; options are JSON text (or null).</summary>
    public static ResolvedModel Model(string id, ModelRole role, string format, IReadOnlyDictionary<string, string> files,
        string? optionsJson = null, bool isTestFixture = false)
    {
        JsonElement? options = optionsJson is null ? null : JsonDocument.Parse(optionsJson).RootElement.Clone();
        var descriptor = new ModelDescriptor
        {
            Id = id,
            Version = "1",
            Role = role,
            Format = format,
            Files = files.ToDictionary(f => f.Key, f => Path.GetFileName(f.Value)),
            Sha256 = files.ToDictionary(f => Path.GetFileName(f.Value), _ => "0000"),
            IsTestFixture = isTestFixture,
            Options = options,
        };
        return new ResolvedModel(descriptor, files);
    }
}
