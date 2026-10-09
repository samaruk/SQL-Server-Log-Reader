using BonikPOS.VoiceSearch.Core.History;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;

namespace BonikPOS.VoiceSearch.Infrastructure.Storage;

/// <summary>Implements "Delete all data": every owner-related file and secret the app created.</summary>
public sealed class PrivacyService(
    OwnerProfileService profiles,
    ISearchHistoryStore history,
    ISecretStore secrets,
    SettingsService settings,
    IAppPaths paths) : IPrivacyService
{
    public const string ImportedCatalogFileName = "imported-catalog.json";

    public async Task DeleteAllDataAsync(CancellationToken cancellationToken)
    {
        await profiles.DeleteAsync(cancellationToken).ConfigureAwait(false);
        await history.ClearAsync(cancellationToken).ConfigureAwait(false);
        await secrets.RemoveAsync(SecretKeys.ApiToken).ConfigureAwait(false);
        foreach (var file in new[]
                 {
                     Path.Combine(paths.CacheDirectory, "catalog-cache.json"),
                     Path.Combine(paths.DataDirectory, ImportedCatalogFileName),
                 })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        await settings.UpdateAsync(_ => new VoiceSearchSettings(), cancellationToken).ConfigureAwait(false);
    }
}
