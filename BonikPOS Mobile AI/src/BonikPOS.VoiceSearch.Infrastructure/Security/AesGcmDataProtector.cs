using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BonikPOS.VoiceSearch.Core.Settings;

namespace BonikPOS.VoiceSearch.Infrastructure.Security;

/// <summary>
/// Authenticated encryption (AES-256-GCM) for small local files. The 256-bit data key is generated on the device and
/// kept in platform secure storage (Android Keystore-backed / iOS Keychain through <see cref="ISecretStore"/>), never
/// next to the data. Envelope: "BVSE" | version (1) | nonce (12) | tag (16) | ciphertext; the purpose string and the
/// header are bound as associated data, so a file cannot be swapped between purposes or modified without detection.
/// Deleting the key makes every copy of the data unreadable (crypto-shredding), including stale backups.
/// </summary>
public sealed class AesGcmDataProtector(ISecretStore secrets, string keyName, string purpose)
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "BVSE"u8.ToArray();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;

    public async Task<byte[]> ProtectAsync(byte[] plaintext)
    {
        var key = await GetOrCreateKeyAsync().ConfigureAwait(false);
        var output = new byte[Magic.Length + 1 + NonceSize + TagSize + plaintext.Length];
        Magic.CopyTo(output, 0);
        output[Magic.Length] = Version;
        var nonce = output.AsSpan(Magic.Length + 1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var tag = output.AsSpan(Magic.Length + 1 + NonceSize, TagSize);
        var cipher = output.AsSpan(Magic.Length + 1 + NonceSize + TagSize);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, AssociatedData(output));
        return output;
    }

    /// <exception cref="CryptographicException">The data was modified, belongs to another purpose, or the key was deleted.</exception>
    public async Task<byte[]> UnprotectAsync(byte[] envelope)
    {
        int header = Magic.Length + 1 + NonceSize + TagSize;
        if (envelope.Length < header || !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("Not a protected BonikPOS file.");
        }

        if (envelope[Magic.Length] != Version)
        {
            throw new CryptographicException($"Unsupported protection version {envelope[Magic.Length]}.");
        }

        var key = await GetKeyAsync().ConfigureAwait(false)
            ?? throw new CryptographicException("The encryption key is missing (data was deleted or the app was reinstalled).");
        var plaintext = new byte[envelope.Length - header];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            envelope.AsSpan(Magic.Length + 1, NonceSize),
            envelope.AsSpan(header),
            envelope.AsSpan(Magic.Length + 1 + NonceSize, TagSize),
            plaintext,
            AssociatedData(envelope));
        return plaintext;
    }

    /// <summary>Removes the key from secure storage. Existing ciphertext becomes permanently unreadable.</summary>
    public async Task DestroyKeyAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }

            await secrets.RemoveAsync(keyName).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private byte[] AssociatedData(ReadOnlySpan<byte> envelope)
    {
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var ad = new byte[Magic.Length + 1 + 4 + purposeBytes.Length];
        envelope[..(Magic.Length + 1)].CopyTo(ad);
        BinaryPrimitives.WriteInt32LittleEndian(ad.AsSpan(Magic.Length + 1), purposeBytes.Length);
        purposeBytes.CopyTo(ad, Magic.Length + 5);
        return ad;
    }

    private async Task<byte[]?> GetKeyAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_key is not null)
            {
                return _key;
            }

            var stored = await secrets.GetAsync(keyName).ConfigureAwait(false);
            if (stored is null)
            {
                return null;
            }

            _key = Convert.FromBase64String(stored);
            return _key;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> GetOrCreateKeyAsync()
    {
        var existing = await GetKeyAsync().ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_key is null)
            {
                var key = RandomNumberGenerator.GetBytes(32);
                await secrets.SetAsync(keyName, Convert.ToBase64String(key)).ConfigureAwait(false);
                _key = key;
            }

            return _key;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Process-memory secret store for tests and for platforms without secure storage. Not persistent.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<string?> GetAsync(string key)
    {
        lock (_gate)
        {
            return Task.FromResult(_values.GetValueOrDefault(key));
        }
    }

    public Task SetAsync(string key, string value)
    {
        lock (_gate)
        {
            _values[key] = value;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        lock (_gate)
        {
            _values.Remove(key);
        }

        return Task.CompletedTask;
    }
}
