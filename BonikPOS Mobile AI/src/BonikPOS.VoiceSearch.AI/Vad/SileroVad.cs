using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Vad;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.AI.Vad;

/// <summary>
/// Silero VAD v5/v6 (MIT). Verified contract (silero-vad 6.2.3): input "input" float [1, 64 context + 512 samples],
/// "state" float [2, 1, 128], optional "sr" int64 scalar; outputs "output" float [1, 1] and "stateN" float [2, 1, 128].
/// The 64-sample context is the tail of the previous input, exactly as the official OnnxWrapper does.
/// </summary>
public sealed class SileroVadModel : IDisposable
{
    public const int SampleRate = 16000;
    public const int ChunkSamples = 512;
    public const int ContextSamples = 64;
    public const int StateSize = 2 * 1 * 128;

    private SileroVadModel(LoadedSession loaded, string modelId, bool hasSampleRateInput)
    {
        Loaded = loaded;
        ModelId = modelId;
        HasSampleRateInput = hasSampleRateInput;
    }

    public LoadedSession Loaded { get; }

    public string ModelId { get; }

    public bool HasSampleRateInput { get; }

    public static SileroVadModel Load(ResolvedModel model, OnnxSessionPreferences preferences, ILogger? logger = null)
    {
        var loaded = OnnxSessionFactory.Create(model.PathOf("model"), preferences.ForStreaming(), allowAccelerator: false, logger);
        try
        {
            var s = loaded.Session;
            TensorContract.RequireInput(s, "input", TensorElementType.Float, -1, -1);
            var input = s.InputMetadata["input"];
            int? width = TensorContract.StaticDim(input, 1);
            if (width is not null && width != ChunkSamples + ContextSamples)
            {
                throw new ModelContractException($"Silero input width must be {ChunkSamples + ContextSamples}, model declares {width}.");
            }

            TensorContract.RequireInput(s, "state", TensorElementType.Float, 2, -1, 128);
            bool hasSr = s.InputMetadata.ContainsKey("sr");
            if (hasSr)
            {
                TensorContract.RequireInput(s, "sr", TensorElementType.Int64);
            }

            TensorContract.RequireNoOtherInputs(s, ["input", "state", "sr"]);
            TensorContract.RequireOutput(s, "output", TensorElementType.Float);
            TensorContract.RequireOutput(s, "stateN", TensorElementType.Float);
            return new SileroVadModel(loaded, model.Descriptor.Id + "@" + model.Descriptor.Version, hasSr);
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    public IVoiceActivityDetector CreateDetector() => new SileroVoiceActivityDetector(this);

    public void Dispose() => Loaded.Dispose();
}

/// <summary>One stateful VAD stream. Tensors are bound once to pinned arrays and reused for every chunk.</summary>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly SileroVadModel _model;
    private readonly float[] _input = new float[SileroVadModel.ContextSamples + SileroVadModel.ChunkSamples];
    private readonly float[] _state = new float[SileroVadModel.StateSize];
    private readonly float[] _stateOut = new float[SileroVadModel.StateSize];
    private readonly float[] _output = new float[1];
    private readonly long[] _sampleRate = [SileroVadModel.SampleRate];
    private readonly string[] _inputNames;
    private readonly OrtValue[] _inputValues;
    private readonly string[] _outputNames = ["output", "stateN"];
    private readonly OrtValue[] _outputValues;
    private readonly RunOptions _runOptions = new();
    private bool _disposed;

    internal SileroVoiceActivityDetector(SileroVadModel model)
    {
        _model = model;
        var input = OrtValue.CreateTensorValueFromMemory(_input, [1, _input.Length]);
        var state = OrtValue.CreateTensorValueFromMemory(_state, [2, 1, 128]);
        if (model.HasSampleRateInput)
        {
            _inputNames = ["input", "state", "sr"];
            _inputValues = [input, state, OrtValue.CreateTensorValueFromMemory(_sampleRate, [])];
        }
        else
        {
            _inputNames = ["input", "state"];
            _inputValues = [input, state];
        }

        _outputValues =
        [
            OrtValue.CreateTensorValueFromMemory(_output, [1, 1]),
            OrtValue.CreateTensorValueFromMemory(_stateOut, [2, 1, 128]),
        ];
    }

    public string ModelId => _model.ModelId;

    public int SampleRate => SileroVadModel.SampleRate;

    public int ChunkSamples => SileroVadModel.ChunkSamples;

    public float Process(ReadOnlySpan<float> chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (chunk.Length != ChunkSamples)
        {
            throw new ArgumentException($"Silero VAD needs exactly {ChunkSamples} samples per chunk.", nameof(chunk));
        }

        // _input[0..64) already holds the context (tail of the previous input).
        chunk.CopyTo(_input.AsSpan(SileroVadModel.ContextSamples));
        _model.Loaded.Session.Run(_runOptions, _inputNames, _inputValues, _outputNames, _outputValues);
        _stateOut.CopyTo(_state, 0);
        _input.AsSpan(_input.Length - SileroVadModel.ContextSamples).CopyTo(_input);
        return _output[0];
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_input);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var v in _inputValues)
        {
            v.Dispose();
        }

        foreach (var v in _outputValues)
        {
            v.Dispose();
        }

        _runOptions.Dispose();
    }
}
