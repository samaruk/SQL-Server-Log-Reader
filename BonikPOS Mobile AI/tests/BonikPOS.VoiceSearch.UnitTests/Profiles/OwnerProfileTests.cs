using System.Text.Json;
using BonikPOS.VoiceSearch.Core.Enrollment;
using BonikPOS.VoiceSearch.Core.Profiles;
using BonikPOS.VoiceSearch.Core.Settings;
using BonikPOS.VoiceSearch.Core.Speakers;
using BonikPOS.VoiceSearch.Infrastructure.Storage;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.Profiles;

public sealed class OwnerProfileTests
{
    private static readonly SpeakerEncoderInfo Ge2e = new("ge2e-resemblyzer", "0.1.4", "f8e45a3d", 3, 16000, 1.6, 0.8)
    {
        Calibration = new SpeakerCalibration { DefaultAcceptThreshold = 0.75, MinAcceptThreshold = 0.68, MaxAcceptThreshold = 0.86 },
    };

    internal static VoiceProfile Profile(string encoderId = "ge2e-resemblyzer", string version = "0.1.4", string sha = "F8E45A3D",
        float[]? centroid = null, double suggested = 0.81, int schema = VoiceProfile.CurrentSchemaVersion) => new()
    {
        SchemaVersion = schema,
        ProfileId = Guid.Parse("1b4e28ba-2fa1-11d2-883f-0016d3cca427"),
        CreatedUtc = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero),
        UpdatedUtc = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero),
        DisplayName = "Owner",
        Consent = new ConsentRecord(new DateTimeOffset(2026, 10, 1, 8, 29, 0, TimeSpan.Zero), "2026-10-v1"),
        Embeddings = [new EnrolledEmbedding(encoderId, version, sha, centroid ?? [0.6f, 0.8f, 0f], 5, 0.91, 0.87, suggested)],
        Enrollment = new EnrollmentSummary(5, 1, 14.2, ["bn", "en"], "0.1.0"),
    };

    [Fact]
    public void Profile_SerializationRoundTrip_PreservesEveryField()
    {
        var original = Profile();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(original, InfrastructureJsonContext.Default.VoiceProfile);
        var copy = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.VoiceProfile)!;

        Assert.Equal(original.SchemaVersion, copy.SchemaVersion);
        Assert.Equal(original.ProfileId, copy.ProfileId);
        Assert.Equal(original.CreatedUtc, copy.CreatedUtc);
        Assert.Equal(original.UpdatedUtc, copy.UpdatedUtc);
        Assert.Equal(original.DisplayName, copy.DisplayName);
        Assert.Equal(original.Consent, copy.Consent);
        var e = Assert.Single(copy.Embeddings);
        var o = original.Embeddings[0];
        Assert.Equal((o.EncoderId, o.EncoderVersion, o.EncoderSha256, o.SampleCount, o.MeanSampleSimilarity, o.MinSampleSimilarity, o.SuggestedAcceptThreshold),
            (e.EncoderId, e.EncoderVersion, e.EncoderSha256, e.SampleCount, e.MeanSampleSimilarity, e.MinSampleSimilarity, e.SuggestedAcceptThreshold));
        Assert.Equal(o.Centroid, e.Centroid);
        Assert.Equal(original.Enrollment.AcceptedSamples, copy.Enrollment.AcceptedSamples);
        Assert.Equal(original.Enrollment.Languages, copy.Enrollment.Languages);
        Assert.Equal(original.Enrollment.TotalSpeechSeconds, copy.Enrollment.TotalSpeechSeconds);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32()); // schema version is persisted explicitly
        Assert.Equal(VoiceProfileCompatibility.Check(original, Ge2e), VoiceProfileCompatibility.Check(copy, Ge2e));
    }

    [Fact]
    public void Compatibility_RequiresTheSameEncoderIdVersionChecksumAndDimension()
    {
        Assert.Equal(ProfileCompatibility.Compatible, VoiceProfileCompatibility.Check(Profile(), Ge2e)); // checksum case-insensitive
        Assert.Equal(ProfileCompatibility.EncoderChanged, VoiceProfileCompatibility.Check(Profile(encoderId: "wespeaker-resnet34"), Ge2e));
        Assert.Equal(ProfileCompatibility.EncoderChanged, VoiceProfileCompatibility.Check(Profile(version: "0.1.5"), Ge2e));
        Assert.Equal(ProfileCompatibility.EncoderChanged, VoiceProfileCompatibility.Check(Profile(sha: "00000000"), Ge2e));
        Assert.Equal(ProfileCompatibility.EncoderChanged, VoiceProfileCompatibility.Check(Profile(centroid: [1f, 0f]), Ge2e));
        Assert.Equal(ProfileCompatibility.SchemaTooNew, VoiceProfileCompatibility.Check(Profile(schema: 2), Ge2e));
        Assert.Equal(ProfileCompatibility.NoProfile, VoiceProfileCompatibility.Check(null, Ge2e));
    }

    [Fact]
    public void BuildReference_OnlyForACompatibleProfile_NeverSilentlyReusingAnotherEncodersEmbedding()
    {
        var settings = new OwnerVerificationSettings();

        Assert.Null(OwnerProfileService.BuildReference(Profile(version: "0.2.0"), Ge2e, settings));
        Assert.Null(OwnerProfileService.BuildReference(Profile(encoderId: "other"), Ge2e, settings));
        Assert.Null(OwnerProfileService.BuildReference(Profile(), null, settings));
        Assert.Null(OwnerProfileService.BuildReference(null, Ge2e, settings));

        var reference = OwnerProfileService.BuildReference(Profile(), Ge2e, settings)!;
        Assert.Equal("ge2e-resemblyzer", reference.Centroid.EncoderId);
        Assert.Equal(0.81, reference.AcceptThreshold, 1e-12);                          // enrollment calibration
        Assert.Equal(0.81 - settings.UncertainBand, reference.RejectThreshold, 1e-12);
        Assert.Equal(0.81 - settings.WindowDrop, reference.WindowFloor, 1e-12);
        Assert.Equal(1.0, reference.Centroid.Vector.Sum(v => (double)v * v), 1e-6);    // unit length

        var overridden = OwnerProfileService.BuildReference(Profile(), Ge2e, settings with { AcceptThresholdOverride = 0.7 })!;
        Assert.Equal(0.7, overridden.AcceptThreshold, 1e-12);
    }

    [Fact]
    public async Task ProfileState_AfterAnEncoderChange_AsksForReEnrollment()
    {
        var store = new InMemoryProfileStore(Profile(version: "0.1.3"));
        var runtime = new FakeRuntime { SpeakerEncoder = new FakeEncoderService(Ge2e) };
        var service = new OwnerProfileService(store, runtime);

        var state = await service.GetStateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProfileCompatibility.EncoderChanged, state.Compatibility);
        Assert.False(state.IsUsable);
        Assert.Contains("re-enroll", state.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(state.Profile); // the profile is kept (the owner decides), but it is not used
    }

    [Fact]
    public async Task ProfileState_CoversMissingEncoderMissingProfileAndCorruption()
    {
        var runtime = new FakeRuntime { SpeakerEncoder = new FakeEncoderService(Ge2e) };
        var ct = TestContext.Current.CancellationToken;

        var ok = await new OwnerProfileService(new InMemoryProfileStore(Profile()), runtime).GetStateAsync(ct);
        Assert.True(ok.IsUsable);

        var noEncoder = await new OwnerProfileService(new InMemoryProfileStore(Profile()), new FakeRuntime()).GetStateAsync(ct);
        Assert.Equal(ProfileCompatibility.EncoderChanged, noEncoder.Compatibility);
        Assert.Contains("not installed", noEncoder.Message);

        var none = await new OwnerProfileService(new InMemoryProfileStore(null), runtime).GetStateAsync(ct);
        Assert.Equal(ProfileCompatibility.NoProfile, none.Compatibility);

        var corrupt = await new OwnerProfileService(new InMemoryProfileStore(null) { Corrupt = true }, runtime).GetStateAsync(ct);
        Assert.Equal(ProfileCompatibility.NoProfile, corrupt.Compatibility);
        Assert.Contains("tampered", corrupt.Message);

        var tooNew = await new OwnerProfileService(new InMemoryProfileStore(Profile(schema: 9)), runtime).GetStateAsync(ct);
        Assert.Equal(ProfileCompatibility.SchemaTooNew, tooNew.Compatibility);
    }

    [Fact]
    public async Task ProfileService_CachesLoads_AndSaveOrDeleteUpdateTheCache()
    {
        var store = new InMemoryProfileStore(null);
        var service = new OwnerProfileService(store, new FakeRuntime());
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await service.GetProfileAsync(ct));
        Assert.Null(await service.GetProfileAsync(ct));
        Assert.Equal(1, store.Loads);

        var profile = Profile();
        await service.SaveAsync(profile, ct);
        Assert.Same(profile, await service.GetProfileAsync(ct));

        await service.DeleteAsync(ct);
        Assert.Null(await service.GetProfileAsync(ct));
        Assert.True(store.Deleted);
        Assert.Equal(1, store.Loads);
    }

    [Fact]
    public void ExtractionTarget_ComesOnlyFromTheEncoderTheExtractionModelRequires()
    {
        var profile = Profile() with
        {
            Embeddings = [Profile().Embeddings[0], new EnrolledEmbedding("wespeaker", "1", "aa", [0f, 1f], 5, 0.9, 0.85, 0.7)],
        };

        Assert.Equal("wespeaker", OwnerProfileService.ExtractionTarget(profile, "wespeaker")!.EncoderId);
        Assert.Null(OwnerProfileService.ExtractionTarget(profile, "ecapa"));
        Assert.Null(OwnerProfileService.ExtractionTarget(profile, null));
        Assert.Null(OwnerProfileService.ExtractionTarget(null, "wespeaker"));
    }

    private sealed class InMemoryProfileStore(VoiceProfile? profile) : IVoiceProfileStore
    {
        private VoiceProfile? _profile = profile;

        public bool Corrupt { get; init; }

        public int Loads { get; private set; }

        public bool Deleted { get; private set; }

        public Task<bool> ExistsAsync(CancellationToken cancellationToken) => Task.FromResult(_profile is not null);

        public Task<VoiceProfile?> LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;
            return Corrupt ? throw new VoiceProfileCorruptException("The voice profile was tampered with.") : Task.FromResult(_profile);
        }

        public Task SaveAsync(VoiceProfile profile, CancellationToken cancellationToken)
        {
            _profile = profile;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            _profile = null;
            Deleted = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEncoderService(SpeakerEncoderInfo info) : ISpeakerEmbeddingService
    {
        public SpeakerEncoderInfo Info { get; } = info;

        public SpeakerEmbedding EmbedUtterance(ReadOnlySpan<float> samples, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IReadOnlyList<WindowEmbedding> EmbedWindows(ReadOnlySpan<float> samples, double windowSeconds, double hopSeconds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}

/// <summary>Leave-one-out calibration of the acceptance threshold, on embeddings with known pairwise similarities.</summary>
public sealed class EnrollmentCalibrationTests
{
    private static readonly SpeakerCalibration Unbounded = new() { MinAcceptThreshold = -1, MaxAcceptThreshold = 1, DefaultAcceptThreshold = 0.75 };

    private static SpeakerEmbedding E(params float[] v) => new("enc", "1", v);

    [Fact]
    public void TwoSamples_EachScoresTheirMutualSimilarity()
    {
        // cos([1,0],[0.8,0.6]) = 0.8 for both leave-one-out scores: mean 0.8, std 0, suggested = min(0.8, 0.8 - 0.03).
        var (mean, min, std, suggested) = EnrollmentSession.Calibrate([E(1, 0), E(0.8f, 0.6f)], Unbounded);

        Assert.Equal(0.8, mean, 1e-6);
        Assert.Equal(0.8, min, 1e-6);
        Assert.Equal(0, std, 1e-6);
        Assert.Equal(0.77, suggested, 1e-6);
    }

    [Fact]
    public void OneOutlier_PullsTheThresholdDownByTwoStandardDeviations()
    {
        // Three identical samples u and one orthogonal sample w:
        //   score(u) = cos(u, normalize(2u + w)) = 2 / sqrt(5); score(w) = cos(w, u) = 0
        //   mean = 3 / (2 sqrt 5), std = sqrt(0.15), suggested = min(mean - 2 std, 0 - 0.03) = mean - 2 std
        var (mean, min, std, suggested) = EnrollmentSession.Calibrate([E(1, 0), E(1, 0), E(1, 0), E(0, 1)], Unbounded);

        double expectedMean = 3 / (2 * Math.Sqrt(5));
        double expectedStd = Math.Sqrt(0.15);
        Assert.Equal(expectedMean, mean, 1e-6);
        Assert.Equal(0, min, 1e-6);
        Assert.Equal(expectedStd, std, 1e-6);
        Assert.Equal(expectedMean - 2 * expectedStd, suggested, 1e-6);
    }

    [Fact]
    public void SuggestedThreshold_IsClampedToTheEncoderLimits()
    {
        var limits = new SpeakerCalibration { MinAcceptThreshold = 0.68, MaxAcceptThreshold = 0.86 };

        Assert.Equal(0.86, EnrollmentSession.Calibrate([E(1, 0), E(1, 0), E(1, 0)], limits).SuggestedThreshold, 1e-9); // 0.97 -> max
        Assert.Equal(0.68, EnrollmentSession.Calibrate([E(1, 0), E(0, 1)], limits).SuggestedThreshold, 1e-9);           // -0.03 -> min
    }

    [Fact]
    public void FewerThanTwoSamples_UseTheEncoderDefault()
    {
        var (mean, min, std, suggested) = EnrollmentSession.Calibrate([E(1, 0)], new SpeakerCalibration { DefaultAcceptThreshold = 0.75 });

        Assert.True(double.IsNaN(mean) && double.IsNaN(min) && double.IsNaN(std));
        Assert.Equal(0.75, suggested);
    }

    [Fact]
    public void EmbeddingsFromDifferentEncoders_CannotBeCalibratedTogether()
    {
        Assert.Throws<InvalidOperationException>(() =>
            EnrollmentSession.Calibrate([E(1, 0), new SpeakerEmbedding("other", "1", [1f, 0f])], Unbounded));
    }

    [Fact]
    public void SpeakerEmbedding_IsNormalised_AndRejectsInvalidVectors()
    {
        var e = E(3, 4);
        Assert.Equal([0.6f, 0.8f], e.Vector);
        Assert.Equal(1.0, e.CosineSimilarity(e), 1e-6);
        Assert.Throws<ArgumentException>(() => E(0, 0));
        Assert.Throws<ArgumentException>(() => E(float.NaN, 1));
        Assert.Throws<ArgumentException>(() => new SpeakerEmbedding("enc", "1", []));
        Assert.Throws<InvalidOperationException>(() => e.CosineSimilarity(new SpeakerEmbedding("other", "1", [3f, 4f])));
        Assert.Equal([0.6f, 0.8f], SpeakerEmbedding.Centroid([E(3, 4), E(6, 8)]).Vector);
    }
}
