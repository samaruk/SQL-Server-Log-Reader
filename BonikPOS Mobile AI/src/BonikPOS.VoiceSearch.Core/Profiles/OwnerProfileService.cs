using BonikPOS.VoiceSearch.Core.Pipeline;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.Core.Profiles;

public sealed record OwnerProfileState(VoiceProfile? Profile, ProfileCompatibility Compatibility, string Message)
{
    public bool IsUsable => Compatibility == ProfileCompatibility.Compatible;
}

/// <summary>Loads the owner profile, checks it against the installed speaker encoder and builds verification references.</summary>
public sealed class OwnerProfileService(IVoiceProfileStore store, IVoiceModelRuntime runtime)
{
    private VoiceProfile? _cached;
    private bool _loaded;

    public async Task<OwnerProfileState> GetStateAsync(CancellationToken cancellationToken)
    {
        VoiceProfile? profile;
        try
        {
            profile = await GetProfileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VoiceProfileCorruptException ex)
        {
            return new OwnerProfileState(null, ProfileCompatibility.NoProfile, ex.Message);
        }

        if (profile is null)
        {
            return new OwnerProfileState(null, ProfileCompatibility.NoProfile, "No owner voice is enrolled.");
        }

        var encoder = runtime.SpeakerEncoder;
        if (encoder is null)
        {
            return new OwnerProfileState(profile, ProfileCompatibility.EncoderChanged,
                "The speaker model is not installed, so the enrolled voice cannot be used.");
        }

        var compatibility = VoiceProfileCompatibility.Check(profile, encoder.Info);
        string message = compatibility switch
        {
            ProfileCompatibility.Compatible => $"Enrolled {profile.CreatedUtc.LocalDateTime:g} with {encoder.Info.ModelId} {encoder.Info.ModelVersion}.",
            ProfileCompatibility.EncoderChanged => "The speaker model changed since enrollment. Please re-enroll your voice.",
            ProfileCompatibility.SchemaTooNew => "The profile was created by a newer app version. Update the app or re-enroll.",
            _ => "No owner voice is enrolled.",
        };
        return new OwnerProfileState(profile, compatibility, message);
    }

    public async Task<VoiceProfile?> GetProfileAsync(CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            _cached = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            _loaded = true;
        }

        return _cached;
    }

    public async Task SaveAsync(VoiceProfile profile, CancellationToken cancellationToken)
    {
        await store.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        _cached = profile;
        _loaded = true;
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        _cached = null;
        _loaded = true;
    }

    /// <summary>Thresholds come from the enrollment calibration unless the owner set an override in settings.</summary>
    public static OwnerVoiceReference? BuildReference(VoiceProfile? profile, SpeakerEncoderInfo? encoder, OwnerVerificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (profile is null || encoder is null || VoiceProfileCompatibility.Check(profile, encoder) != ProfileCompatibility.Compatible)
        {
            return null;
        }

        var enrolled = profile.FindEmbedding(encoder.ModelId)!;
        double accept = settings.AcceptThresholdOverride ?? enrolled.SuggestedAcceptThreshold;
        return new OwnerVoiceReference(enrolled.ToEmbedding(), accept, accept - settings.UncertainBand, accept - settings.WindowDrop);
    }

    /// <summary>The owner's embedding from the encoder a TSE model requires, if the profile has one.</summary>
    public static SpeakerEmbedding? ExtractionTarget(VoiceProfile? profile, string? requiredEncoderId) =>
        profile is null || requiredEncoderId is null ? null : profile.FindEmbedding(requiredEncoderId)?.ToEmbedding();
}
