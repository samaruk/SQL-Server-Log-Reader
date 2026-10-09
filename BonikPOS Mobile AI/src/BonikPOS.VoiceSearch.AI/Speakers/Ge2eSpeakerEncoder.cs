using BonikPOS.VoiceSearch.AI.Dsp;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Audio;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Speakers;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.AI.Speakers;

/// <summary>
/// GE2E d-vector encoder (Resemblyzer 0.1.4 weights exported by tools/models/export_ge2e_onnx.py). Reproduces
/// Resemblyzer's embed_utterance: power mel (n_fft 400, hop 160, 40 Slaney mels), 160-frame partials at 1.3 partials
/// per second, mean of the partial embeddings, L2 normalisation. Input loudness is normalised to -30 dBFS
/// (increase only), as Resemblyzer's preprocess_wav does.
/// </summary>
public sealed class Ge2eSpeakerEncoder : ISpeakerEmbeddingService
{
    private readonly LoadedSession _loaded;
    private readonly RunOptions _runOptions = new();
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _mels;
    private readonly int _hop;
    private readonly int _partialFrames;
    private readonly int _dimension;
    private readonly double _partialRate;
    private readonly double _minCoverage;
    private readonly double? _normalizeDbfs;
    private readonly Lock _featureGate = new();
    private readonly PowerMelSpectrogram _features;

    private Ge2eSpeakerEncoder(LoadedSession loaded, ModelDescriptor descriptor, string sha256)
    {
        _loaded = loaded;
        var o = new ModelOptions(descriptor);
        _inputName = o.String("input", "mels");
        _outputName = o.String("output", "embedding");
        int rate = o.Int("sample_rate", 16000);
        _mels = o.Int("n_mels", 40);
        _hop = o.Int("hop_length", 160);
        int fft = o.Int("n_fft", 400);
        _partialFrames = o.Int("partial_frames", 160);
        _dimension = o.RequiredInt("embedding_dim");
        _partialRate = o.Double("partials_per_second", 1.3);
        _minCoverage = o.Double("min_coverage", 0.75);
        double normalize = o.Double("normalize_dbfs", -30);
        _normalizeDbfs = double.IsNaN(normalize) || normalize == 0 ? null : normalize;
        _features = new PowerMelSpectrogram(rate, fft, _hop, _mels, CenterPadMode.Constant);

        var s = loaded.Session;
        TensorContract.RequireInput(s, _inputName, TensorElementType.Float, -1, -1, _mels);
        TensorContract.RequireNoOtherInputs(s, [_inputName]);
        TensorContract.RequireOutput(s, _outputName, TensorElementType.Float, -1, _dimension);
        Info = new SpeakerEncoderInfo(
            descriptor.Id, descriptor.Version, sha256, _dimension, rate,
            WindowSeconds: (double)_partialFrames * _hop / rate,
            MinimumSpeechSeconds: o.Double("min_speech_seconds", 0.8))
        {
            Calibration = SpeakerCalibrationOptions.Read(o.Section("verification")),
        };
    }

    public SpeakerEncoderInfo Info { get; }

    public string ExecutionProvider => _loaded.ExecutionProvider;

    public double InitMilliseconds => _loaded.InitMilliseconds;

    public static Ge2eSpeakerEncoder Load(ResolvedModel model, OnnxSessionPreferences preferences, ILogger? logger = null)
    {
        string path = model.PathOf("model");
        var loaded = OnnxSessionFactory.Create(path, preferences, allowAccelerator: false, logger);
        try
        {
            string sha = model.Descriptor.Sha256.Values.FirstOrDefault() ?? string.Empty;
            return new Ge2eSpeakerEncoder(loaded, model.Descriptor, sha);
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    public SpeakerEmbedding EmbedUtterance(ReadOnlySpan<float> samples, CancellationToken cancellationToken = default)
    {
        var wav = Prepare(samples);
        var slices = PartialSlices(wav.Length, _partialRate, _minCoverage);
        int needed = slices[^1] * _hop + _partialFrames * _hop;
        if (needed > wav.Length)
        {
            Array.Resize(ref wav, needed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mel = ComputeMel(wav);
        var embeddings = RunPartials(mel, slices);
        var mean = new float[_dimension];
        for (int p = 0; p < slices.Count; p++)
        {
            for (int d = 0; d < _dimension; d++)
            {
                mean[d] += embeddings[p * _dimension + d];
            }
        }

        return new SpeakerEmbedding(Info.ModelId, Info.ModelVersion, mean);
    }

    public IReadOnlyList<WindowEmbedding> EmbedWindows(
        ReadOnlySpan<float> samples, double windowSeconds, double hopSeconds, CancellationToken cancellationToken = default)
    {
        var wav = Prepare(samples);
        int rate = Info.SampleRate;
        int windowFrames = Math.Max(10, (int)Math.Round(windowSeconds * rate / _hop));
        int hopFrames = Math.Max(1, (int)Math.Round(hopSeconds * rate / _hop));
        int totalFrames = wav.Length / _hop;
        var starts = new List<int>();
        for (int start = 0; start + windowFrames <= Math.Max(totalFrames, windowFrames); start += hopFrames)
        {
            starts.Add(start);
            if (start + windowFrames >= totalFrames)
            {
                break;
            }
        }

        int needed = (starts[^1] + windowFrames) * _hop;
        if (needed > wav.Length)
        {
            Array.Resize(ref wav, needed);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mel = ComputeMel(wav);
        var embeddings = RunPartials(mel, starts, windowFrames);
        var result = new List<WindowEmbedding>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
        {
            var e = new SpeakerEmbedding(Info.ModelId, Info.ModelVersion, embeddings.AsSpan(i * _dimension, _dimension).ToArray());
            result.Add(new WindowEmbedding(starts[i] * _hop, Math.Min(windowFrames * _hop, Math.Max(0, samples.Length - starts[i] * _hop)), e));
        }

        return result;
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _loaded.Dispose();
    }

    /// <summary>Port of Resemblyzer VoiceEncoder.compute_partial_slices; returns the start frame of each partial.</summary>
    internal List<int> PartialSlices(int samples, double rate, double minCoverage)
    {
        int samplesPerFrame = _hop;
        int nFrames = (int)Math.Ceiling((samples + 1) / (double)samplesPerFrame);
        int frameStep = (int)Math.Round(Info.SampleRate / rate / samplesPerFrame, MidpointRounding.ToEven);
        int steps = Math.Max(1, nFrames - _partialFrames + frameStep + 1);
        var starts = new List<int>();
        for (int i = 0; i < steps; i += frameStep)
        {
            starts.Add(i);
        }

        int lastStart = starts[^1] * samplesPerFrame;
        double coverage = (double)(samples - lastStart) / (_partialFrames * samplesPerFrame);
        if (coverage < minCoverage && starts.Count > 1)
        {
            starts.RemoveAt(starts.Count - 1);
        }

        return starts;
    }

    private float[] Prepare(ReadOnlySpan<float> samples)
    {
        if (samples.Length < _hop * 10)
        {
            throw new ArgumentException("Utterance is too short for a speaker embedding.", nameof(samples));
        }

        var wav = samples.ToArray();
        if (_normalizeDbfs is double target)
        {
            double rms = AudioLevel.Rms(wav);
            if (rms > 0)
            {
                double change = target - AudioLevel.ToDbfs(rms);
                if (change > 0)
                {
                    float gain = (float)Math.Pow(10, change / 20);
                    for (int i = 0; i < wav.Length; i++)
                    {
                        wav[i] *= gain;
                    }
                }
            }
        }

        return wav;
    }

    private float[] ComputeMel(float[] wav)
    {
        lock (_featureGate)
        {
            return _features.Compute(wav);
        }
    }

    private float[] RunPartials(float[] mel, IReadOnlyList<int> starts, int? framesOverride = null)
    {
        int frames = framesOverride ?? _partialFrames;
        int totalFrames = mel.Length / _mels;
        var batch = new float[starts.Count * frames * _mels];
        for (int p = 0; p < starts.Count; p++)
        {
            int available = Math.Clamp(totalFrames - starts[p], 0, frames);
            mel.AsSpan(starts[p] * _mels, available * _mels).CopyTo(batch.AsSpan(p * frames * _mels));
        }

        using var input = OrtValue.CreateTensorValueFromMemory(batch, [starts.Count, frames, _mels]);
        using var outputs = _loaded.Session.Run(_runOptions, new[] { _inputName }, new[] { input }, new[] { _outputName });
        return outputs[0].GetTensorDataAsSpan<float>().ToArray();
    }
}
