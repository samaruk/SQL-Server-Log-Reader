using BonikPOS.VoiceSearch.AI.Dsp;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Extraction;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Speakers;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.AI.Extraction;

/// <summary>
/// A loaded, validated STFT-domain streaming TSE model (format "streaming-tse-stft-onnx/1"), e.g. pGTCRN
/// (tools/models/tse). The model processes one STFT frame per call: <c>spec_input</c> [1, n_fft/2+1, 1, 2] (real,
/// imaginary) plus the target embedding and recurrent state tensors, and returns the enhanced frame. The adapter does
/// the STFT, inverse STFT and overlap-add. Options: <c>embedding_dim</c> and <c>required_encoder_id</c> (required);
/// <c>n_fft</c> (512), <c>hop</c> (256), <c>window</c> ("sqrt_hann_periodic"), <c>spec_input</c> ("mix"),
/// <c>spec_output</c> ("enh"), <c>embedding_input</c> ("spk_emb"), <c>states</c> (array of {input, output}; default:
/// every other input X paired with output "X_out"). All tensor shapes are read from the ONNX graph and checked.
/// </summary>
public sealed class StftStreamingTseModel : ITseModel
{
    public const string FormatId = "streaming-tse-stft-onnx/1";

    private StftStreamingTseModel(LoadedSession loaded, ModelDescriptor descriptor)
    {
        Loaded = loaded;
        Descriptor = descriptor;
        var o = new ModelOptions(descriptor);
        var s = loaded.Session;
        NFft = o.Int("n_fft", 512);
        Hop = o.Int("hop", 256);
        if (NFft != 2 * Hop)
        {
            throw new ModelContractException($"Only 50% overlap is supported (n_fft {NFft}, hop {Hop}).");
        }

        if (o.String("window", "sqrt_hann_periodic") != "sqrt_hann_periodic")
        {
            throw new ModelContractException("Only the periodic square-root Hann window is supported.");
        }

        if (o.Int("sample_rate", 16000) != 16000)
        {
            throw new ModelContractException("Only 16 kHz target-speaker extraction models are supported by the pipeline.");
        }

        SpecInput = o.String("spec_input", "mix");
        SpecOutput = o.String("spec_output", "enh");
        EmbeddingInput = o.String("embedding_input", "spk_emb");
        EmbeddingDim = o.RequiredInt("embedding_dim");
        RequiredEncoderId = o.RequiredString("required_encoder_id");
        Bins = NFft / 2 + 1;
        TensorContract.RequireInput(s, SpecInput, TensorElementType.Float, 1, Bins, 1, 2);
        TensorContract.RequireOutput(s, SpecOutput, TensorElementType.Float, 1, Bins, 1, 2);
        TensorContract.RequireInput(s, EmbeddingInput, TensorElementType.Float, 1, EmbeddingDim);

        var pairs = o.Array("states").Select(e => (e.GetProperty("input").GetString()!, e.GetProperty("output").GetString()!)).ToList();
        if (pairs.Count == 0)
        {
            pairs = [.. s.InputMetadata.Keys.Where(k => k != SpecInput && k != EmbeddingInput).Select(k => (k, k + "_out"))];
        }

        var states = new List<StreamingStateSpec>();
        foreach (var (inName, outName) in pairs)
        {
            var meta = TensorContract.RequireInput(s, inName, TensorElementType.Float);
            TensorContract.RequireOutput(s, outName, TensorElementType.Float, [.. meta.Dimensions.Select(d => (int?)d)]);
            if (meta.Dimensions.Any(d => d <= 0))
            {
                throw new ModelContractException($"State '{inName}' has dynamic dimensions; the streaming adapter needs static state shapes.");
            }

            states.Add(new StreamingStateSpec(inName, outName, [.. meta.Dimensions.Select(d => (long)d)]));
        }

        States = states;
        TensorContract.RequireNoOtherInputs(s, [SpecInput, EmbeddingInput, .. states.Select(x => x.InputName)]);
    }

    public LoadedSession Loaded { get; }

    public ModelDescriptor Descriptor { get; }

    public int NFft { get; }

    public int Hop { get; }

    public int Bins { get; }

    public string SpecInput { get; }

    public string SpecOutput { get; }

    public string EmbeddingInput { get; }

    public int EmbeddingDim { get; }

    public string RequiredEncoderId { get; }

    public IReadOnlyList<StreamingStateSpec> States { get; }

    public int ChunkSamples => Hop;

    /// <summary>Overlap-add delay (n_fft - hop). The extractor adds a constant alignment delay of up to hop - 1.</summary>
    public int LatencySamples => NFft - Hop;

    public string Description => $"STFT-domain streaming target-speaker extraction ({NFft}-point frames, {Hop}-sample hop)";

    public static StftStreamingTseModel Load(ResolvedModel model, OnnxSessionPreferences preferences, ILogger? logger = null)
    {
        var loaded = OnnxSessionFactory.Create(model.PathOf("model"), preferences.ForStreaming(), allowAccelerator: true, logger);
        try
        {
            return new StftStreamingTseModel(loaded, model.Descriptor);
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    public ITargetSpeakerExtractor CreateExtractor(ILogger? logger) => new StftStreamingTargetSpeakerExtractor(this, logger);

    public void Dispose() => Loaded.Dispose();
}

/// <summary>
/// Streams audio through an STFT-domain TSE model: every <c>hop</c> new samples form one analysis frame (periodic
/// sqrt-Hann window), the model enhances that frame given the owner embedding and its recurrent state, and the inverse
/// FFT is overlap-added with the same window. All buffers are preallocated; ONNX inputs/outputs are bound to them once,
/// so the steady state allocates nothing. Called from the pipeline worker thread, never from the microphone callback.
/// </summary>
public sealed class StftStreamingTargetSpeakerExtractor : ITargetSpeakerExtractor
{
    private readonly StftStreamingTseModel _model;
    private readonly ILogger? _logger;
    private readonly Fft _fft;
    private readonly double[] _window;
    private readonly double[] _frame;
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly double[] _outRe;
    private readonly double[] _outIm;
    private readonly double[] _ola;
    private readonly float[] _hopBuffer;
    private readonly float[] _spec;
    private readonly float[] _enhanced;
    private readonly float[] _embedding;
    private readonly float[][] _stateIn;
    private readonly float[][] _stateOut;
    private readonly string[] _inputNames;
    private readonly OrtValue[] _inputValues;
    private readonly string[] _outputNames;
    private readonly OrtValue[] _outputValues;
    private readonly RunOptions _runOptions = new();
    private readonly FloatFifo _pending;
    private int _hopFill;
    private int? _alignmentDelay;
    private bool _hasTarget;
    private bool _failed;
    private string _message;

    public StftStreamingTargetSpeakerExtractor(StftStreamingTseModel model, ILogger? logger = null)
    {
        _model = model;
        _logger = logger;
        int n = model.NFft;
        _fft = new Fft(n);
        _window = new double[n];
        for (int i = 0; i < n; i++)
        {
            _window[i] = Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n)); // periodic Hann, square-rooted
        }

        _frame = new double[n];
        _re = new double[n];
        _im = new double[n];
        _outRe = new double[n];
        _outIm = new double[n];
        _ola = new double[n];
        _hopBuffer = new float[model.Hop];
        _spec = new float[model.Bins * 2];
        _enhanced = new float[model.Bins * 2];
        _embedding = new float[model.EmbeddingDim];
        _stateIn = [.. model.States.Select(s => new float[s.Length])];
        _stateOut = [.. model.States.Select(s => new float[s.Length])];
        _inputNames = [model.SpecInput, model.EmbeddingInput, .. model.States.Select(s => s.InputName)];
        _inputValues =
        [
            OrtValue.CreateTensorValueFromMemory(_spec, [1, model.Bins, 1, 2]),
            OrtValue.CreateTensorValueFromMemory(_embedding, [1, model.EmbeddingDim]),
            .. model.States.Select((s, i) => OrtValue.CreateTensorValueFromMemory(_stateIn[i], s.Shape)),
        ];
        _outputNames = [model.SpecOutput, .. model.States.Select(s => s.OutputName)];
        _outputValues =
        [
            OrtValue.CreateTensorValueFromMemory(_enhanced, [1, model.Bins, 1, 2]),
            .. model.States.Select((s, i) => OrtValue.CreateTensorValueFromMemory(_stateOut[i], s.Shape)),
        ];
        _pending = new FloatFifo(model.NFft * 4);
        _message = "Waiting for the owner's voice profile.";
    }

    public TargetSpeakerExtractorStatus Status => new(
        _failed ? TargetSpeakerExtractionAvailability.Failed
            : _hasTarget ? TargetSpeakerExtractionAvailability.Active
            : TargetSpeakerExtractionAvailability.NeedsReEnrollment,
        _model.Descriptor.Id,
        _model.RequiredEncoderId,
        _model.Hop,
        _model.LatencySamples + (_alignmentDelay ?? 0),
        _message);

    /// <summary>Number of model frames run since the last reset (diagnostics and benchmarks).</summary>
    public long FramesProcessed { get; private set; }

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
            // Output is produced in hop-sized blocks; a constant delay keeps every call's output length equal to its input.
            // (The n_fft - hop overlap-add latency is already part of the emitted blocks.)
            _alignmentDelay = input.Length % _model.Hop == 0 ? 0 : _model.Hop - 1;
            _pending.EnqueueZeros(_alignmentDelay.Value);
        }

        try
        {
            int i = 0;
            while (i < input.Length)
            {
                int take = Math.Min(_model.Hop - _hopFill, input.Length - i);
                input.Slice(i, take).CopyTo(_hopBuffer.AsSpan(_hopFill));
                _hopFill += take;
                i += take;
                if (_hopFill == _model.Hop)
                {
                    RunFrame();
                    _hopFill = 0;
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

        _pending.Dequeue(output[..input.Length]);
    }

    public void Reset()
    {
        foreach (var s in _stateIn)
        {
            Array.Clear(s);
        }

        Array.Clear(_frame);
        Array.Clear(_ola);
        _pending.Clear();
        _hopFill = 0;
        _alignmentDelay = null;
        FramesProcessed = 0;
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

    /// <summary>Analysis frame -> model -> synthesis; emits <c>hop</c> finished samples. Latency = n_fft - hop.</summary>
    private void RunFrame()
    {
        int n = _model.NFft, hop = _model.Hop, bins = _model.Bins;
        Array.Copy(_frame, hop, _frame, 0, n - hop);
        for (int i = 0; i < hop; i++)
        {
            _frame[n - hop + i] = _hopBuffer[i];
        }

        for (int i = 0; i < n; i++)
        {
            _re[i] = _frame[i] * _window[i];
            _im[i] = 0;
        }

        _fft.Forward(_re, _im, _outRe, _outIm);
        for (int k = 0; k < bins; k++)
        {
            _spec[2 * k] = (float)_outRe[k];
            _spec[2 * k + 1] = (float)_outIm[k];
        }

        _model.Loaded.Session.Run(_runOptions, _inputNames, _inputValues, _outputNames, _outputValues);
        for (int i = 0; i < _stateIn.Length; i++)
        {
            _stateOut[i].CopyTo(_stateIn[i], 0);
        }

        // Inverse real FFT via the forward transform: x = conj(FFT(conj(X))) / N with a Hermitian-symmetric spectrum.
        for (int k = 0; k < bins; k++)
        {
            _re[k] = _enhanced[2 * k];
            _im[k] = -_enhanced[2 * k + 1];
        }

        for (int k = bins; k < n; k++)
        {
            _re[k] = _enhanced[2 * (n - k)];
            _im[k] = _enhanced[2 * (n - k) + 1];
        }

        // DC and Nyquist bins are real for a real signal (numpy.fft.irfft ignores their imaginary parts).
        _im[0] = 0;
        _im[n / 2] = 0;
        _fft.Forward(_re, _im, _outRe, _outIm);
        for (int i = 0; i < n; i++)
        {
            _ola[i] += _outRe[i] / n * _window[i];
        }

        for (int i = 0; i < hop; i++)
        {
            _pending.Enqueue((float)_ola[i]);
        }

        Array.Copy(_ola, hop, _ola, 0, n - hop);
        Array.Clear(_ola, n - hop, hop);
        FramesProcessed++;
    }

    /// <summary>Fixed-capacity single-threaded FIFO of samples.</summary>
    private sealed class FloatFifo(int capacity)
    {
        private float[] _buffer = new float[capacity];
        private int _head;
        private int _count;

        public void Enqueue(float v)
        {
            EnsureCapacity(_count + 1);
            _buffer[(_head + _count) % _buffer.Length] = v;
            _count++;
        }

        public void EnqueueZeros(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Enqueue(0f);
            }
        }

        public void Dequeue(Span<float> destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                if (_count == 0)
                {
                    destination[i] = 0f; // cannot happen with the alignment delay; defensive
                    continue;
                }

                destination[i] = _buffer[_head];
                _head = (_head + 1) % _buffer.Length;
                _count--;
            }
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buffer.Length)
            {
                return;
            }

            var bigger = new float[Math.Max(needed, _buffer.Length * 2)];
            for (int i = 0; i < _count; i++)
            {
                bigger[i] = _buffer[(_head + i) % _buffer.Length];
            }

            _buffer = bigger;
            _head = 0;
        }
    }
}
