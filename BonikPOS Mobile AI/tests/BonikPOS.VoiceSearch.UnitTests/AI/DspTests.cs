using BonikPOS.VoiceSearch.AI.Dsp;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;

namespace BonikPOS.VoiceSearch.UnitTests.AI;

/// <summary>Self-consistency of the DSP front-ends (parity with librosa / Kaldi / Whisper is in the integration tests).</summary>
public sealed class DspTests
{
    private static (double[] Re, double[] Im) NaiveDft(double[] re, double[] im)
    {
        int n = re.Length;
        var outRe = new double[n];
        var outIm = new double[n];
        for (int k = 0; k < n; k++)
        {
            for (int t = 0; t < n; t++)
            {
                double a = -2 * Math.PI * ((long)k * t % n) / n;
                outRe[k] += re[t] * Math.Cos(a) - im[t] * Math.Sin(a);
                outIm[k] += re[t] * Math.Sin(a) + im[t] * Math.Cos(a);
            }
        }

        return (outRe, outIm);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(400)]  // Whisper / librosa n_fft = 4*4*5*5
    [InlineData(512)]  // Kaldi padded window
    [InlineData(1001)] // 7*11*13
    public void MixedRadixFft_MatchesTheDefinition(int size)
    {
        var rng = new Random(size);
        var re = Enumerable.Range(0, size).Select(_ => rng.NextDouble() * 2 - 1).ToArray();
        var im = Enumerable.Range(0, size).Select(_ => rng.NextDouble() * 2 - 1).ToArray();
        var outRe = new double[size];
        var outIm = new double[size];

        new Fft(size).Forward(re, im, outRe, outIm);
        var (expRe, expIm) = NaiveDft(re, im);

        for (int k = 0; k < size; k++)
        {
            Assert.Equal(expRe[k], outRe[k], 1e-9 * size);
            Assert.Equal(expIm[k], outIm[k], 1e-9 * size);
        }
    }

    [Fact]
    public void Fft_RejectsSizesWithLargePrimeFactors_AndWrongBufferLengths()
    {
        Assert.Throws<ArgumentException>(() => new Fft(17 * 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(0));
        var fft = new Fft(8);
        Assert.Throws<ArgumentException>(() => fft.Forward(new double[8], new double[8], new double[4], new double[8]));
    }

    [Fact]
    public void RealPowerSpectrum_TwoFramePacking_EqualsSeparateTransforms()
    {
        var a = Signals.Noise(400, seed: 1).Select(x => (double)x).ToArray();
        var b = Signals.Sine(16000, 1234, 400 / 16000.0).Select(x => (double)x).ToArray();
        var spectrum = new RealPowerSpectrum(400);
        var powerA = new double[201];
        var powerB = new double[201];
        spectrum.Compute(a, b, powerA, powerB);

        foreach (var (frame, power) in new[] { (a, powerA), (b, powerB) })
        {
            var (re, im) = NaiveDft(frame, new double[400]);
            for (int k = 0; k <= 200; k++)
            {
                Assert.Equal(re[k] * re[k] + im[k] * im[k], power[k], 1e-7);
            }
        }
    }

    [Fact]
    public void SlaneyMelScale_RoundTrips_AndIsLinearBelow1kHz()
    {
        foreach (double hz in new[] { 0.0, 200, 999.9, 1000, 4000, 8000 })
        {
            Assert.Equal(hz, MelFilterBank.SlaneyMelToHz(MelFilterBank.HzToSlaneyMel(hz)), 1e-6);
        }

        Assert.Equal(15.0, MelFilterBank.HzToSlaneyMel(1000), 1e-12); // 1000 Hz / (200/3) Hz per mel
        Assert.Equal(1127.0 * Math.Log(1 + 1000 / 700.0), MelFilterBank.KaldiMel(1000), 1e-12);
    }

    [Fact]
    public void SlaneyFilterBank_IsAreaNormalisedAndCoversTheSpectrum()
    {
        var bank = MelFilterBank.Slaney(16000, 400, 80);
        Assert.Equal(80, bank.Mels);
        Assert.Equal(201, bank.Bins);
        Assert.All(bank.Weights, w => Assert.True(w >= 0));
        for (int m = 0; m < bank.Mels; m++)
        {
            Assert.Contains(bank.Weights.Skip(m * bank.Bins).Take(bank.Bins), w => w > 0);
        }

        // Apply() equals a dense matrix-vector product.
        var power = Enumerable.Range(0, 201).Select(i => 1.0 + i % 7).ToArray();
        var output = new double[80];
        bank.Apply(power, output);
        for (int m = 0; m < 80; m++)
        {
            double expected = 0;
            for (int b = 0; b < 201; b++)
            {
                expected += bank.Weights[m * 201 + b] * power[b];
            }

            Assert.Equal(expected, output[m], 1e-9);
        }
    }

    [Fact]
    public void StftPower_HasCenteredFrameCount_AndZeroFramesForSilence()
    {
        var stft = new StftPower(400, 160, CenterPadMode.Reflect);
        Assert.Equal(201, stft.Bins);
        Assert.Equal(1 + 16000 / 160, stft.FrameCount(16000));
        var power = new double[3 * 201];
        stft.Compute(new float[480], 3, power);
        Assert.All(power, p => Assert.Equal(0, p));
    }

    [Fact]
    public void KaldiFbank_UsesSnipEdgesFraming()
    {
        var fbank = new KaldiFbank(new KaldiFbankOptions());
        Assert.Equal(0, fbank.FrameCount(399));
        Assert.Equal(1, fbank.FrameCount(400));
        Assert.Equal(1 + (16000 - 400) / 160, fbank.FrameCount(16000));
        Assert.Empty(fbank.Compute(new float[399]));

        // Digital silence hits the FLT_EPSILON floor of the log.
        var silent = fbank.Compute(new float[800]);
        Assert.Equal(fbank.FrameCount(800) * 80, silent.Length);
        Assert.All(silent, v => Assert.Equal((float)Math.Log(1.1920928955078125e-07), v));
    }

    [Fact]
    public void KaldiFbank_SubtractMean_CentresEveryBin()
    {
        var feats = Signals.Noise(10 * 4, seed: 3);
        KaldiFbank.SubtractMean(feats, 4);
        for (int bin = 0; bin < 4; bin++)
        {
            double mean = Enumerable.Range(0, 10).Average(t => feats[t * 4 + bin]);
            Assert.Equal(0, mean, 1e-6);
        }
    }

    [Fact]
    public void WhisperLogMel_ClampsTheDynamicRangeTo8Decades_AndValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WhisperLogMel(40));
        var mel = new WhisperLogMel(80);
        Assert.Throws<ArgumentException>(() => mel.Compute(new float[100], 300, new float[80 * 299]));

        var output = new float[80 * 300];
        var audio = Signals.Sine(16000, 440, 1.0, 0.5); // shorter than 3 s: zero padded
        mel.Compute(audio, 300, output);

        float max = output.Max(), min = output.Min();
        Assert.Equal(2.0, max - min, 1e-5); // (log10 range of 8) / 4
        Assert.All(output, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void PowerMelSpectrogram_ScalesWithThePowerOfTheInput()
    {
        var spec = new PowerMelSpectrogram(16000, 400, 160, 40);
        var x = Signals.Noise(4000, seed: 9, amplitude: 0.1);
        var a = spec.Compute(x);
        var b = spec.Compute(x.Select(v => v * 2).ToArray());

        Assert.Equal(spec.FrameCount(4000) * 40, a.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(4 * a[i], b[i], Math.Max(1e-9, a[i] * 1e-5)); // not logarithmic: doubling amplitude quadruples power
        }
    }
}
