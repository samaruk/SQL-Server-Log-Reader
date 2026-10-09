using BonikPOS.VoiceSearch.Core.Settings;

namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>In-memory <see cref="ISecretStore"/> fake that also records which keys were removed.</summary>
public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public List<string> Removed { get; } = [];

    public IReadOnlyDictionary<string, string> Values => _values;

    public Task<string?> GetAsync(string key)
    {
        lock (_values)
        {
            return Task.FromResult(_values.GetValueOrDefault(key));
        }
    }

    public Task SetAsync(string key, string value)
    {
        lock (_values)
        {
            _values[key] = value;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        lock (_values)
        {
            _values.Remove(key);
            Removed.Add(key);
        }

        return Task.CompletedTask;
    }
}
