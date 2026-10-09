using System.Text.Json;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Speakers;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.AI.Extraction;

/// <summary>One recurrent state tensor pair of a streaming model.</summary>
public sealed record StreamingStateSpec(string InputName, string OutputName, long[] Shape)
{
    public int Length => (int)Shape.Aggregate(1L, (a, b) => a * b);
}

/// <summary>
/// The loaded, validated TSE model (format "streaming-tse-onnx/1"). Its manifest declares every tensor; the adapter
/// checks the declaration against the ONNX graph, so no tensor dimension is assumed. Required options:
/// <c>chunk_samples</c>, <c>embedding_dim</c>, <c>required_encoder_id</c>; optional: <c>audio_input</c>,
/// <c>embedding_input</c>, <c>audio_output</c>, <c>states</c> (array of {input, output, shape}),
/// <c>algorithmic_latency_samples</c>. See docs/MODELS.md "Integrating a target-speaker extraction model".
/// </summary>
public sealed class StreamingTseModel : IDisposable
{
    private StreamingTseModel(LoadedSession loaded, ModelDescriptor descriptor)
    {
        Loaded = loaded;
        Descriptor = descriptor;
        var o = new ModelOptions(descriptor);
        var s = loaded.Session;
        AudioInput = o.String("audio_input", "mixture");
        EmbeddingInput = o.String("embedding_input", "spk_emb");
        AudioOutput = o.String("audio_output", "enhanced");
        ChunkSamples = o.RequiredInt("chunk_samples");
        EmbeddingDim = o.RequiredInt("embedding_dim");
        if (ChunkSamples <= 0 || EmbeddingDim <= 0)
        {
            throw new ModelContractException(
                $"Manifest entry '{descriptor.Id}' must declare positive chunk_samples and embedding_dim (got {ChunkSamples} and {EmbeddingDim}).");
        }

        RequiredEncoderId = o.RequiredString("required_encoder_id");
        LatencySamples = o.Int("algorithmic_latency_samples", 0);
        if (o.Int("sample_rate", 16000) != 16000)
        {
            throw new ModelContractException("Only 16 kHz target-speaker extraction models are supported by the pipeline.");
        }

        var audio = TensorContract.RequireInput(s, AudioInput, TensorElementType.Float, 1, -1);
        int? declared = TensorContract.StaticDim(audio, 1);
        if (declared is not null && declared != ChunkSamples)
        {
            throw new ModelContractException($"Manifest chunk_samples {ChunkSamples} does not match the model's {AudioInput} width {declared}.");
        }

        TensorContract.RequireInput(s, EmbeddingInput, TensorElementType.Float, 1, EmbeddingDim);
        TensorContract.RequireOutput(s, AudioOutput, TensorElementType.Float, 1, ChunkSamples);

        var states = new List<StreamingStateSpec>();
        foreach (var st in o.Array("states"))
        {
            if (st.ValueKind != JsonValueKind.Object
                || !st.TryGetProperty("input", out var inJson) || inJson.ValueKind != JsonValueKind.String
                || !st.TryGetProperty("output", out var outJson) || outJson.ValueKind != JsonValueKind.String)
            {
                throw new ModelContractException(
                    $"Each entry of 'states' in manifest entry '{descriptor.Id}' must be an object with string 'input' and 'output' names: {st.GetRawText()}");
            }

            string inName = inJson.GetString()!;
            string outName = outJson.GetString()!;
            var meta = TensorContract.RequireInput(s, inName, TensorElementType.Float);
            TensorContract.RequireOutput(s, outName, TensorElementType.Float);
            bool hasShape = st.TryGetProperty("shape", out var shapeJson);
            if (hasShape && (shapeJson.ValueKind != JsonValueKind.Array || shapeJson.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out _))))
            {
                throw new ModelContractException($"State '{inName}' shape must be an array of integers, not {shapeJson.GetRawText()}.");
            }

            long[] shape = hasShape
                ? [.. shapeJson.EnumerateArray().Select(e => e.GetInt64())]
                : [.. meta.Dimensions.Select(d => (long)d)];
            if (shape.Any(d => d <= 0))
            {
                throw new ModelContractException($"State '{inName}' has dynamic dimensions; declare its shape in the manifest.");
            }

            if (meta.Dimensions.Length != shape.Length || meta.Dimensions.Where((d, i) => d > 0 && d != shape[i]).Any())
            {
                throw new ModelContractException($"State '{inName}' shape [{string.Join(",", shape)}] does not match the model.");
            }

            states.Add(new StreamingStateSpec(inName, outName, shape));
        }

        States = states;
        TensorContract.RequireNoOtherInputs(s, [AudioInput, EmbeddingInput, .. states.Select(x => x.InputName)]);
    }

    public LoadedSession Loaded { get; }

    public ModelDescriptor Descriptor { get; }

    public string AudioInput { get; }

    public string EmbeddingInput { get; }

    public string AudioOutput { get; }

    public int ChunkSamples { get; }

    public int EmbeddingDim { get; }

    public string RequiredEncoderId { get; }

    public int LatencySamples { get; }

    public IReadOnlyList<StreamingStateSpec> States { get; }

    public static StreamingTseModel Load(ResolvedModel model, OnnxSessionPreferences preferences, ILogger? logger = null)
    {
        var loaded = OnnxSessionFactory.Create(model.PathOf("model"), preferences, allowAccelerator: true, logger);
        try
        {
            return new StreamingTseModel(loaded, model.Descriptor);
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    public void Dispose() => Loaded.Dispose();
}

/// <summary>
/// Streaming target-speaker extraction with a stateful ONNX model. Audio is processed chunk by chunk as it arrives
/// (never as one large recording); recurrent state tensors are carried from each chunk to the next in preallocated,
/// pinned buffers. Output is delayed by a fixed number of samples so that it always has exactly the input's length.
/// </summary>
public sealed class OnnxStreamingTargetSpeakerExtractor : ITargetSpeakerExtractor
{
    private readonly StreamingTseModel _model;
    private readonly ILogger? _logger;
    private readonly float[] _chunk;
    private readonly float[] _embedding;
    private readonly float[] _enhanced;
    private readonly float[][] _stateIn;
    private readonly float[][] _stateOut;
    private readonly string[] _inputNames;
    private readonly OrtValue[] _inputValues;
    private readonly string[] _outputNames;
    private readonly OrtValue[] _outputValues;
    private readonly RunOptions _runOptions = new();
    private readonly Queue<float> _pending = new();
    private int _chunkFill;
    private int? _alignmentDelay;
    private bool _hasTarget;
    private bool _failed;
    private string _message;

    public OnnxStreamingTargetSpeakerExtractor(StreamingTseModel model, ILogger? logger = null)
    {
        _model = model;
        _logger = logger;
        _chunk = new float[model.ChunkSamples];
        _embedding = new float[model.EmbeddingDim];
        _enhanced = new float[model.ChunkSamples];
        _stateIn = [.. model.States.Select(s => new float[s.Length])];
        _stateOut = [.. model.States.Select(s => new float[s.Length])];
        _inputNames = [model.AudioInput, model.EmbeddingInput, .. model.States.Select(s => s.InputName)];
        _inputValues =
        [
            OrtValue.CreateTensorValueFromMemory(_chunk, [1, model.ChunkSamples]),
            OrtValue.CreateTensorValueFromMemory(_embedding, [1, model.EmbeddingDim]),
            .. model.States.Select((s, i) => OrtValue.CreateTensorValueFromMemory(_stateIn[i], s.Shape)),
        ];
        _outputNames = [model.AudioOutput, .. model.States.Select(s => s.OutputName)];
        _outputValues =
        [
            OrtValue.CreateTensorValueFromMemory(_enhanced, [1, model.ChunkSamples]),
            .. model.States.Select((s, i) => OrtValue.CreateTensorValueFromMemory(_stateOut[i], s.Shape)),
        ];
        _message = "Waiting for the owner's voice profile.";
    }

    public TargetSpeakerExtractorStatus Status => new(
        _failed ? TargetSpeakerExtractionAvailability.Failed
            : _hasTarget ? TargetSpeakerExtractionAvailability.Active
            : TargetSpeakerExtractionAvailability.NeedsReEnrollment,
        _model.Descriptor.Id,
        _model.RequiredEncoderId,
        _model.ChunkSamples,
        _model.LatencySamples + (_alignmentDelay ?? 0),
        _message);

    public void SetTarget(SpeakerEmbedding? target)
    {
        if (target is null)
        {
            _hasTarget = false;
            _message = "No owner embedding from " + _model.RequiredEncoderId + " is available; re-enroll to activate extraction.";
            return;
        }

        if (target.EncoderId != _model.RequiredEncoderId || target.Dimension != _model.EmbeddingDim)
        {
            _hasTarget = false;
            _message = $"The model needs a {_model.EmbeddingDim}-dim embedding from {_model.RequiredEncoderId}, " +
                       $"but the profile has {target.Dimension}-dim from {target.EncoderId}. Re-enroll.";
            return;
        }

        target.Vector.CopyTo(_embedding, 0);
        _hasTarget = true;
        _message = "Target-speaker extraction is conditioned on the enrolled owner.";
        Reset();
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Output must be at least as long as input.", nameof(output));
        }

        if (!_hasTarget || _failed)
        {
            input.CopyTo(output);
            return;
        }

        if (_alignmentDelay is null)
        {
            _alignmentDelay = input.Length % _model.ChunkSamples == 0 ? 0 : _model.ChunkSamples - 1;
            for (int i = 0; i < _alignmentDelay; i++)
            {
                _pending.Enqueue(0f);
            }
        }

        try
        {
            foreach (float s in input)
            {
                _chunk[_chunkFill++] = s;
                if (_chunkFill == _chunk.Length)
                {
                    RunChunk();
                    _chunkFill = 0;
                }
            }
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException)
        {
            _failed = true;
            _message = "Target-speaker extraction failed at runtime and was switched off: " + ex.Message;
            _logger?.LogError(ex, "TSE model failed");
            input.CopyTo(output);
            return;
        }

        for (int i = 0; i < input.Length; i++)
        {
            output[i] = _pending.Count > 0 ? _pending.Dequeue() : 0f;
        }
    }

    public void Reset()
    {
        foreach (var s in _stateIn)
        {
            Array.Clear(s);
        }

        _pending.Clear();
        _chunkFill = 0;
        _alignmentDelay = null;
    }

    public void Dispose()
    {
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

    private void RunChunk()
    {
        _model.Loaded.Session.Run(_runOptions, _inputNames, _inputValues, _outputNames, _outputValues);
        for (int i = 0; i < _stateIn.Length; i++)
        {
            _stateOut[i].CopyTo(_stateIn[i], 0);
        }

        foreach (float s in _enhanced)
        {
            _pending.Enqueue(s);
        }
    }
}
