using System.Security.Cryptography;
using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Infrastructure.Security;

namespace BonikPOS.VoiceSearch.Infrastructure.Storage;

/// <summary>
/// Stores the owner voice profile (biometric-derived embeddings) encrypted with AES-256-GCM in the private data
/// directory. The key lives in platform secure storage; <see cref="DeleteAsync"/> removes both the file and the key.
/// </summary>
public sealed class EncryptedVoiceProfileStore : IVoiceProfileStore
{
    public const string FileName = "owner-voice-profile.bin";
    private readonly string _path;
    private readonly AesGcmDataProtector _protector;

    public EncryptedVoiceProfileStore(IAppPaths paths, ISecretStore secrets)
    {
        _path = Path.Combine(paths.DataDirectory, FileName);
        _protector = new AesGcmDataProtector(secrets, SecretKeys.ProfileDataKey, "bonikpos.voice-profile.v1");
    }

    public string FilePath => _path;

    public Task<bool> ExistsAsync(CancellationToken cancellationToken) => Task.FromResult(File.Exists(_path));

    public async Task<VoiceProfile?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[] plaintext;
        try
        {
            var envelope = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            plaintext = await _protector.UnprotectAsync(envelope).ConfigureAwait(false);
        }
        catch (CryptographicException ex)
        {
            throw new VoiceProfileCorruptException("The voice profile could not be decrypted (modified, or its key was removed). Re-enroll.", ex);
        }

        try
        {
            return JsonSerializer.Deserialize(plaintext, InfrastructureJsonContext.Default.VoiceProfile)
                   ?? throw new VoiceProfileCorruptException("The voice profile is empty.");
        }
        catch (JsonException ex)
        {
            throw new VoiceProfileCorruptException("The voice profile has an unknown format. Re-enroll.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task SaveAsync(VoiceProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(profile, InfrastructureJsonContext.Default.VoiceProfile);
        try
        {
            var envelope = await _protector.ProtectAsync(plaintext).ConfigureAwait(false);
            await AtomicFile.WriteAsync(_path, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_path))
        {
            // Overwrite before deleting; on flash storage this is best effort, the key removal is what guarantees it.
            var length = new FileInfo(_path).Length;
            await File.WriteAllBytesAsync(_path, new byte[length], cancellationToken).ConfigureAwait(false);
            File.Delete(_path);
        }

        await _protector.DestroyKeyAsync().ConfigureAwait(false);
    }
}
