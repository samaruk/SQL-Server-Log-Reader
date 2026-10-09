namespace BonikPOS.VoiceSearch.Core.Settings;

/// <summary>Caches the current settings and notifies listeners when they change.</summary>
public sealed class SettingsService(ISettingsStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VoiceSearchSettings? _current;

    public event EventHandler<VoiceSearchSettings>? Changed;

    public VoiceSearchSettings Current => _current ?? new VoiceSearchSettings();

    public async Task<VoiceSearchSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (_current is not null)
        {
            return _current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _current ??= await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VoiceSearchSettings> UpdateAsync(Func<VoiceSearchSettings, VoiceSearchSettings> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        VoiceSearchSettings updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _current ?? await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            updated = change(current);
            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _current = updated;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, updated);
        return updated;
    }
}
