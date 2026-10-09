using BonikPOS.VoiceSearch.AI.Speakers;
using BonikPOS.VoiceSearch.Core.Speakers;

namespace BonikPOS.VoiceSearch.UnitTests.AI;

/// <summary>Decision logic of the verifier on top of a fake encoder that returns chosen embeddings.</summary>
public sealed class OwnerVoiceVerifierTests
{
    private static readonly OwnerVoiceReference Reference = new(new SpeakerEmbedding("enc", "1", [1f, 0f]), 0.80, 0.72, 0.65);

    /// <summary>Unit vector whose cosine with [1, 0] is <paramref name="cosine"/>.</summary>
    private static float[] At(double cosine) => [(float)cosine, (float)Math.Sqrt(1 - cosine * cosine)];

    private sealed class FakeEncoder(double utteranceCosine, params double[] windowCosines) : ISpeakerEmbeddingService
    {
        public int UtteranceCalls { get; private set; }

        public int WindowCalls { get; private set; }

        public SpeakerEncoderInfo Info { get; } = new("enc", "1", "abc", 2, 16000, WindowSeconds: 1.0, MinimumSpeechSeconds: 0.5);

        public SpeakerEmbedding EmbedUtterance(ReadOnlySpan<float> samples, CancellationToken cancellationToken = default)
        {
            UtteranceCalls++;
            return new SpeakerEmbedding("enc", "1", At(utteranceCosine));
        }

        public IReadOnlyList<WindowEmbedding> EmbedWindows(ReadOnlySpan<float> samples, double windowSeconds, double hopSeconds, CancellationToken cancellationToken = default)
        {
            WindowCalls++;
            Assert.Equal(1.0, windowSeconds);
            Assert.Equal(0.5, hopSeconds);
            return [.. windowCosines.Select((c, i) => new WindowEmbedding(i * 8000, 16000, new SpeakerEmbedding("enc", "1", At(c))))];
        }

        public void Dispose()
        {
        }
    }

    private static OwnerVerificationResult Verify(FakeEncoder encoder, double seconds, OwnerVoiceReference? reference = null) =>
        new OwnerVoiceVerifier(encoder).Verify(new float[(int)(seconds * 16000)], reference ?? Reference, CancellationToken.None);

    [Theory]
    [InlineData(0.95, OwnerVoiceDecision.Owner)]
    [InlineData(0.801, OwnerVoiceDecision.Owner)]     // just above the accept threshold (0.80)
    [InlineData(0.799, OwnerVoiceDecision.Uncertain)] // between reject (0.72) and accept
    [InlineData(0.721, OwnerVoiceDecision.Uncertain)]
    [InlineData(0.719, OwnerVoiceDecision.NotOwner)]
    [InlineData(0.50, OwnerVoiceDecision.NotOwner)]
    public void ShortUtterance_IsDecidedOnTheWholeUtteranceSimilarity(double cosine, OwnerVoiceDecision expected)
    {
        var encoder = new FakeEncoder(cosine);

        var result = Verify(encoder, seconds: 1.2); // shorter than 1.5 windows: no window analysis

        Assert.Equal(expected, result.Decision);
        Assert.Equal(cosine, result.Similarity, 1e-6);
        Assert.Equal(1, result.Windows);
        Assert.Equal(0, encoder.WindowCalls);
        Assert.False(result.OtherVoiceSuspected);
        Assert.Equal("enc", result.EncoderId);
    }

    [Fact]
    public void TooLittleSpeech_IsInsufficient_WithoutRunningTheEncoder()
    {
        var encoder = new FakeEncoder(0.99);

        var result = Verify(encoder, seconds: 0.4);

        Assert.Equal(OwnerVoiceDecision.InsufficientSpeech, result.Decision);
        Assert.True(double.IsNaN(result.Similarity));
        Assert.Equal(0, encoder.UtteranceCalls);
    }

    [Fact]
    public void AWindowFarBelowTheOwner_InsideAnAcceptedUtterance_FlagsASecondVoice()
    {
        var encoder = new FakeEncoder(0.90, 0.97, 0.93, 0.50);

        var result = Verify(encoder, seconds: 2.0);

        Assert.Equal(OwnerVoiceDecision.Uncertain, result.Decision);
        Assert.True(result.OtherVoiceSuspected);
        Assert.Equal(3, result.Windows);
        Assert.Equal(0.50, result.MinWindowSimilarity, 1e-6);
        Assert.Equal(2.0 / 3, result.OwnerWindowFraction, 1e-9);
        Assert.Contains("another voice", result.Reason);
    }

    [Fact]
    public void ConsistentWindows_KeepTheOwnerDecision()
    {
        var result = Verify(new FakeEncoder(0.90, 0.88, 0.86, 0.91), seconds: 2.0);

        Assert.Equal(OwnerVoiceDecision.Owner, result.Decision);
        Assert.False(result.OtherVoiceSuspected);
        Assert.Equal(1.0, result.OwnerWindowFraction);
    }

    [Fact]
    public void ReferenceFromAnotherEncoder_RequiresReEnrollment_InsteadOfBeingCompared()
    {
        var encoder = new FakeEncoder(0.99);
        var foreign = Reference with { Centroid = new SpeakerEmbedding("other-encoder", "1", [1f, 0f]) };
        var wrongDim = Reference with { Centroid = new SpeakerEmbedding("enc", "1", [1f, 0f, 0f]) };

        foreach (var reference in new[] { foreign, wrongDim })
        {
            var result = Verify(encoder, 2.0, reference);
            Assert.Equal(OwnerVoiceDecision.NoProfile, result.Decision);
            Assert.Contains("re-enrollment", result.Reason);
        }

        Assert.Equal(0, encoder.UtteranceCalls);
    }
}
