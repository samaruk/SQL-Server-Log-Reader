using BonikPOS.VoiceSearch.AI.Dsp;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Speakers;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.AI.Speakers;

/// <summary>
/// Speaker encoders that consume Kaldi fbank features, such as WeSpeaker (ResNet34 / CAM++ / ECAPA) and 3D-Speaker
/// exports. Front-end parameters come from the manifest because they differ per family: WeSpeaker exports expect
/// int16-range samples (input_scale 32768) and per-utterance mean subtraction; 3D-Speaker exports expect [-1, 1]
/// samples (input_scale 1). Contract: input float [1, frames, bins] -> output float [1, embedding_dim].
/// </summary>
public sealed class FbankSpeakerEncoder : ISpeakerEmbeddingService
{
    private readonly LoadedSession _loaded;
    private readonly RunOptions _runOptions = new();
    private readonly KaldiFbank _fbank;
    private readonly Lock _featureGate = new();
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _bins;
    private readonly int _dimension;
    private readonly bool _cmn;

    private FbankSpeakerEncoder(LoadedSession loaded, ModelDescriptor descriptor, string sha256)
    {
        _loaded = loaded;
        var o = new ModelOptions(descriptor);
        _inputName = o.String("input", "feats");
        _outputName = o.String("output", "embs");
        _bins = o.Int("num_bins", 80);
        _dimension = o.RequiredInt("embedding_dim");
        _cmn = o.Bool("subtract_mean", true);
        var window = o.String("window", "povey").ToLowerInvariant() switch
        {
            "hamming" => KaldiWindowType.Hamming,
            "hanning" or "hann" => KaldiWindowType.Hanning,
            _ => KaldiWindowType.Povey,
        };
        _fbank = new KaldiFbank(new KaldiFbankOptions
        {
            SampleRate = o.Int("sample_rate", 16000),
            NumBins = _bins,
            Window = window,
            InputScale = (float)o.Double("input_scale", 32768),
            PreemphCoeff = o.Double("preemph", 0.97),
            RemoveDcOffset = o.Bool("remove_dc_offset", true),
            LowFreq = o.Double("low_freq", 20),
            HighFreq = o.Double("high_freq", 0),
        });

        var s = loaded.Session;
        TensorContract.RequireInput(s, _inputName, TensorElementType.Float, -1, -1, _bins);
        TensorContract.RequireNoOtherInputs(s, [_inputName]);
        TensorContract.RequireOutput(s, _outputName, TensorElementType.Float, -1, _dimension);
        Info = new SpeakerEncoderInfo(
            descriptor.Id, descriptor.Version, sha256, _dimension, o.Int("sample_rate", 16000),
            WindowSeconds: o.Double("window_seconds", 1.5),
            MinimumSpeechSeconds: o.Double("min_speech_seconds", 0.8))
        {
            Calibration = SpeakerCalibrationOptions.Read(o.Section("verification")),
        };
    }

    public SpeakerEncoderInfo Info { get; }

    public string ExecutionProvider => _loaded.ExecutionProvider;

    public double InitMilliseconds => _loaded.InitMilliseconds;

    public static FbankSpeakerEncoder Load(ResolvedModel model, OnnxSessionPreferences preferences, ILogger? logger = null)
    {
        var loaded = OnnxSessionFactory.Create(model.PathOf("model"), preferences, allowAccelerator: false, logger);
        try
        {
            return new FbankSpeakerEncoder(loaded, model.Descriptor, model.Descriptor.Sha256.Values.FirstOrDefault() ?? string.Empty);
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    public SpeakerEmbedding EmbedUtterance(ReadOnlySpan<float> samples, CancellationToken cancellationToken = default)
    {
        var feats = Features(samples);
        return Run(feats, 0, feats.Length / _bins);
    }

    public IReadOnlyList<WindowEmbedding> EmbedWindows(
        ReadOnlySpan<float> samples, double windowSeconds, double hopSeconds, CancellationToken cancellationToken = default)
    {
        float[] feats;
        lock (_featureGate)
        {
            feats = _fbank.Compute(samples);
        }

        int total = feats.Length / _bins;
        int window = Math.Max(20, (int)Math.Round(windowSeconds * 100));
        int hop = Math.Max(1, (int)Math.Round(hopSeconds * 100));
        var result = new List<WindowEmbedding>();
        for (int start = 0; start < Math.Max(1, total - window + 1); start += hop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int len = Math.Min(window, total - start);
            var slice = feats.AsSpan(start * _bins, len * _bins).ToArray();
            if (_cmn)
            {
                KaldiFbank.SubtractMean(slice, _bins);
            }

            result.Add(new WindowEmbedding(start * 160, len * 160, Run(slice, 0, len)));
            if (start + window >= total)
            {
                break;
            }
        }

        return result;
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _loaded.Dispose();
    }

    private float[] Features(ReadOnlySpan<float> samples)
    {
        float[] feats;
        lock (_featureGate)
        {
            feats = _fbank.Compute(samples);
        }

        if (feats.Length == 0)
        {
            throw new ArgumentException("Utterance is too short for a speaker embedding.", nameof(samples));
        }

        if (_cmn)
        {
            KaldiFbank.SubtractMean(feats, _bins);
        }

        return feats;
    }

    private SpeakerEmbedding Run(float[] feats, int startFrame, int frames)
    {
        var data = startFrame == 0 && frames * _bins == feats.Length ? feats : feats.AsSpan(startFrame * _bins, frames * _bins).ToArray();
        using var input = OrtValue.CreateTensorValueFromMemory(data, [1, frames, _bins]);
        using var outputs = _loaded.Session.Run(_runOptions, new[] { _inputName }, new[] { input }, new[] { _outputName });
        return new SpeakerEmbedding(Info.ModelId, Info.ModelVersion, outputs[0].GetTensorDataAsSpan<float>().ToArray());
    }
}
