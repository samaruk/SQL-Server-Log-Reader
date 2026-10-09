using System.Security.Cryptography;
using System.Text;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Infrastructure.Security;
using BonikPOS.VoiceSearch.Infrastructure.Storage;
using BonikPOS.VoiceSearch.UnitTests.Profiles;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Infrastructure;

public sealed class AesGcmDataProtectorTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("owner embedding: 0.12, -0.53, 0.77 ...");

    [Fact]
    public async Task ProtectThenUnprotect_RoundTrips_AndUsesAFreshNonceEveryTime()
    {
        var secrets = new FakeSecretStore();
        var protector = new AesGcmDataProtector(secrets, "key.a", "purpose.a");

        var first = await protector.ProtectAsync(Secret);
        var second = await protector.ProtectAsync(Secret);

        Assert.Equal(Secret, await protector.UnprotectAsync(first));
        Assert.Equal(Secret, await protector.UnprotectAsync(second));
        Assert.NotEqual(first, second);                                           // random nonce
        Assert.Equal("BVSE"u8.ToArray(), first.Take(4));
        Assert.Equal(1, first[4]);                                                // envelope version
        Assert.Equal(4 + 1 + 12 + 16 + Secret.Length, first.Length);
        Assert.False(first.AsSpan().IndexOf(Secret.AsSpan(0, 16)) >= 0);         // ciphertext, not plaintext
        Assert.Equal(32, Convert.FromBase64String(secrets.Values["key.a"]).Length); // a 256-bit key lives in secure storage only
    }

    [Theory]
    [InlineData(0)]   // magic
    [InlineData(4)]   // version
    [InlineData(5)]   // nonce
    [InlineData(20)]  // tag
    [InlineData(33)]  // ciphertext
    [InlineData(-1)]  // last ciphertext byte
    public async Task AnyModifiedByte_IsDetected(int index)
    {
        var protector = new AesGcmDataProtector(new FakeSecretStore(), "key", "purpose");
        var envelope = await protector.ProtectAsync(Secret);
        int i = index < 0 ? envelope.Length + index : index;
        envelope[i] ^= 0x01;

        await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.UnprotectAsync(envelope));
    }

    [Fact]
    public async Task TruncatedOrForeignData_IsRejected()
    {
        var protector = new AesGcmDataProtector(new FakeSecretStore(), "key", "purpose");
        var envelope = await protector.ProtectAsync(Secret);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.UnprotectAsync(envelope[..20]));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.UnprotectAsync(envelope[..^1]));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.UnprotectAsync(Encoding.UTF8.GetBytes("{\"plain\": \"json file\"}")));
    }

    [Fact]
    public async Task DataProtectedForOnePurpose_CannotBeReadUnderAnother_EvenWithTheSameKey()
    {
        var secrets = new FakeSecretStore();
        var profileProtector = new AesGcmDataProtector(secrets, "shared.key", "bonikpos.voice-profile.v1");
        var historyProtector = new AesGcmDataProtector(secrets, "shared.key", "bonikpos.search-history.v1");

        var envelope = await profileProtector.ProtectAsync(Secret);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => historyProtector.UnprotectAsync(envelope));
        Assert.Equal(Secret, await profileProtector.UnprotectAsync(envelope));
    }

    [Fact]
    public async Task DataProtectedWithAnotherKey_CannotBeRead()
    {
        var mine = new AesGcmDataProtector(new FakeSecretStore(), "key", "purpose");
        var other = new AesGcmDataProtector(new FakeSecretStore(), "key", "purpose");
        await other.ProtectAsync([1, 2, 3]); // the other device / installation has its own key

        var envelope = await mine.ProtectAsync(Secret);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => other.UnprotectAsync(envelope));
    }

    [Fact]
    public async Task DestroyKey_MakesEveryExistingCopyUnreadable_CryptoShredding()
    {
        var secrets = new FakeSecretStore();
        var protector = new AesGcmDataProtector(secrets, "key", "purpose");
        var envelope = await protector.ProtectAsync(Secret);
        var backupCopy = envelope.ToArray();

        await protector.DestroyKeyAsync();

        Assert.Contains("key", secrets.Removed);
        Assert.False(secrets.Values.ContainsKey("key"));
        var ex = await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.UnprotectAsync(backupCopy));
        Assert.Contains("key is missing", ex.Message);

        // New data gets a new key; the old copy stays unreadable for good.
        var fresh = await protector.ProtectAsync(Secret);
        Assert.Equal(Secret, await protector.UnprotectAsync(fresh));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => protector.UnprotectAsync(backupCopy));
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            new AesGcmDataProtector(secrets, "key", "purpose").UnprotectAsync(backupCopy));
    }

    [Fact]
    public async Task KeyIsReusedAcrossInstances_SoDataSurvivesAnAppRestart()
    {
        var secrets = new FakeSecretStore();
        var envelope = await new AesGcmDataProtector(secrets, "key", "purpose").ProtectAsync(Secret);

        Assert.Equal(Secret, await new AesGcmDataProtector(secrets, "key", "purpose").UnprotectAsync(envelope));
    }
}

public sealed class EncryptedVoiceProfileStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeSecretStore _secrets = new();

    public void Dispose() => _dir.Dispose();

    private EncryptedVoiceProfileStore NewStore() => new(_dir.AppPaths(), _secrets);

    [Fact]
    public async Task SaveLoadDelete_RoundTripsThroughAnEncryptedFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        Assert.False(await store.ExistsAsync(ct));
        Assert.Null(await store.LoadAsync(ct));

        var profile = OwnerProfileTests.Profile();
        await store.SaveAsync(profile, ct);

        Assert.True(await store.ExistsAsync(ct));
        Assert.Equal(Path.Combine(_dir.Path, "data", EncryptedVoiceProfileStore.FileName), store.FilePath);
        var bytes = await File.ReadAllBytesAsync(store.FilePath, ct);
        Assert.Equal("BVSE"u8.ToArray(), bytes.Take(4));
        Assert.True(bytes.AsSpan().IndexOf("ge2e-resemblyzer"u8) < 0, "the profile must not be stored in clear text");
        Assert.True(_secrets.Values.ContainsKey(SecretKeys.ProfileDataKey));
        Assert.False(File.Exists(store.FilePath + ".tmp"));

        var loaded = await NewStore().LoadAsync(ct); // a new instance, as after an app restart
        Assert.NotNull(loaded);
        Assert.Equal(profile.ProfileId, loaded!.ProfileId);
        Assert.Equal(profile.Embeddings[0].Centroid, loaded.Embeddings[0].Centroid);
        Assert.Equal(profile.Consent, loaded.Consent);

        await store.DeleteAsync(ct);
        Assert.False(File.Exists(store.FilePath));
        Assert.Contains(SecretKeys.ProfileDataKey, _secrets.Removed);
        Assert.Null(await NewStore().LoadAsync(ct));
    }

    [Fact]
    public async Task TamperedFile_IsReportedAsCorrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        await store.SaveAsync(OwnerProfileTests.Profile(), ct);
        var bytes = await File.ReadAllBytesAsync(store.FilePath, ct);
        bytes[^5] ^= 0x40;
        await File.WriteAllBytesAsync(store.FilePath, bytes, ct);

        var ex = await Assert.ThrowsAsync<VoiceProfileCorruptException>(() => store.LoadAsync(ct));
        Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public async Task ARestoredBackupCopy_IsUnreadableAfterDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        await store.SaveAsync(OwnerProfileTests.Profile(), ct);
        var backup = await File.ReadAllBytesAsync(store.FilePath, ct);

        await store.DeleteAsync(ct);
        await File.WriteAllBytesAsync(store.FilePath, backup, ct); // e.g. restored from a device backup

        await Assert.ThrowsAsync<VoiceProfileCorruptException>(() => NewStore().LoadAsync(ct));
    }

    [Fact]
    public async Task ValidEnvelopeWithUnknownContent_IsReportedAsCorrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        var protector = new AesGcmDataProtector(_secrets, SecretKeys.ProfileDataKey, "bonikpos.voice-profile.v1");
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllBytesAsync(store.FilePath, await protector.ProtectAsync("not json at all"u8.ToArray()), ct);

        var ex = await Assert.ThrowsAsync<VoiceProfileCorruptException>(() => store.LoadAsync(ct));
        Assert.Contains("unknown format", ex.Message);
    }
}
