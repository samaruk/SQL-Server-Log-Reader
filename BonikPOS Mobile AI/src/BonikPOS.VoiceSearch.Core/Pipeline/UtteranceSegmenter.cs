using BonikPOS.VoiceSearch.Core.Vad;

namespace BonikPOS.VoiceSearch.Core.Pipeline;

/// <summary>A finished utterance cut out of the stream by <see cref="UtteranceSegmenter"/>.</summary>
/// <param name="Audio">Speech plus pre-roll and trailing silence, for speech recognition.</param>
/// <param name="SpeechOnly">Only the VAD-positive chunks, for speaker verification.</param>
/// <param name="EndDetectedTimestamp">Stopwatch timestamp at which the end of speech was detected (latency reference).</param>
public sealed record Utterance(
    float[] Audio,
    float[] SpeechOnly,
    int SampleRate,
    long FirstCaptureTimestamp,
    long EndDetectedTimestamp,
    double SpeechMilliseconds,
    bool Truncated);

public sealed record UtteranceSegmenterOptions
{
    public double StartThreshold { get; init; } = 0.5;

    public double EndThreshold { get; init; } = 0.35;

    public int MinSpeechMilliseconds { get; init; } = 250;

    public int EndSilenceMilliseconds { get; init; } = 500;

    public int PreRollMilliseconds { get; init; } = 300;

    public int MaxUtteranceMilliseconds { get; init; } = 8000;

    /// <summary>Consecutive VAD-positive chunks needed to open an utterance (debounces clicks).</summary>
    public int StartChunks { get; init; } = 2;
}

/// <summary>
/// Streaming end-pointer: runs the VAD on fixed chunks and cuts utterances with hysteresis, pre-roll and a hang-over.
/// All buffers are preallocated; only a finished utterance allocates its own arrays.
/// </summary>
public sealed class UtteranceSegmenter
{
    private readonly IVoiceActivityDetector _vad;
    private readonly UtteranceSegmenterOptions _options;
    private readonly float[] _chunk;
    private readonly float[] _preRoll;
    private readonly float[] _utterance;
    private readonly float[] _speechOnly;
    private readonly int _endSilenceChunks;
    private int _chunkFill;
    private int _preRollWrite;
    private int _preRollCount;
    private int _utteranceLength;
    private int _speechOnlyLength;
    private int _speechRun;
    private int _silenceRun;
    private int _speechChunks;
    private bool _inSpeech;
    private long _firstTimestamp;
    private long _chunkTimestamp;

    public UtteranceSegmenter(IVoiceActivityDetector vad, UtteranceSegmenterOptions options)
    {
        ArgumentNullException.ThrowIfNull(vad);
        ArgumentNullException.ThrowIfNull(options);
        if (options.EndThreshold > options.StartThreshold)
        {
            throw new ArgumentException("EndThreshold must not exceed StartThreshold.", nameof(options));
        }

        _vad = vad;
        _options = options;
        int rate = vad.SampleRate;
        _chunk = new float[vad.ChunkSamples];
        int preRollSamples = Math.Max(vad.ChunkSamples * options.StartChunks, rate * options.PreRollMilliseconds / 1000);
        _preRoll = new float[preRollSamples];
        int maxSamples = rate * options.MaxUtteranceMilliseconds / 1000;
        _utterance = new float[maxSamples + preRollSamples];
        _speechOnly = new float[maxSamples + preRollSamples];
        _endSilenceChunks = Math.Max(1, (int)Math.Ceiling(options.EndSilenceMilliseconds * rate / 1000.0 / vad.ChunkSamples));
    }

    public bool InSpeech => _inSpeech;

    /// <summary>Speech probability of the most recent chunk.</summary>
    public float LastProbability { get; private set; }

    public double ChunkMilliseconds => 1000.0 * _vad.ChunkSamples / _vad.SampleRate;

    /// <summary>
    /// Feeds samples. Returns a finished utterance or null. At most one utterance can end per call when the call
    /// carries no more than one VAD chunk worth of audio (the pipeline feeds 20 ms frames).
    /// </summary>
    public Utterance? Push(ReadOnlySpan<float> samples, long captureTimestamp)
    {
        Utterance? finished = null;
        while (!samples.IsEmpty)
        {
            if (_chunkFill == 0)
            {
                _chunkTimestamp = captureTimestamp;
            }

            int take = Math.Min(samples.Length, _chunk.Length - _chunkFill);
            samples[..take].CopyTo(_chunk.AsSpan(_chunkFill));
            _chunkFill += take;
            samples = samples[take..];
            if (_chunkFill < _chunk.Length)
            {
                break;
            }

            _chunkFill = 0;
            var done = ProcessChunk();
            finished ??= done;
        }

        return finished;
    }

    /// <summary>Ends a pending utterance (for example when capture stops while the owner is still speaking).</summary>
    public Utterance? Flush()
    {
        return _inSpeech ? Finish(truncated: false) : null;
    }

    /// <summary>Clears all state, including the VAD's recurrent state (after a capture discontinuity).</summary>
    public void Reset()
    {
        _vad.Reset();
        _chunkFill = 0;
        _preRollWrite = 0;
        _preRollCount = 0;
        _utteranceLength = 0;
        _speechOnlyLength = 0;
        _speechRun = 0;
        _silenceRun = 0;
        _speechChunks = 0;
        _inSpeech = false;
        LastProbability = 0;
    }

    private Utterance? ProcessChunk()
    {
        float p = _vad.Process(_chunk);
        LastProbability = p;
        if (!_inSpeech)
        {
            AppendPreRoll(_chunk);
            _speechRun = p >= _options.StartThreshold ? _speechRun + 1 : 0;
            if (_speechRun >= _options.StartChunks)
            {
                _inSpeech = true;
                _silenceRun = 0;
                _speechChunks = _speechRun;
                _firstTimestamp = _chunkTimestamp;
                CopyPreRollToUtterance();
            }

            return null;
        }

        Append(_utterance, ref _utteranceLength, _chunk);
        if (p >= _options.EndThreshold)
        {
            _speechChunks++;
            _silenceRun = 0;
            Append(_speechOnly, ref _speechOnlyLength, _chunk);
        }
        else
        {
            _silenceRun++;
        }

        if (_silenceRun >= _endSilenceChunks)
        {
            return Finish(truncated: false);
        }

        if (_utteranceLength + _chunk.Length > _utterance.Length)
        {
            return Finish(truncated: true);
        }

        return null;
    }

    private Utterance? Finish(bool truncated)
    {
        double speechMs = _speechChunks * ChunkMilliseconds;
        Utterance? result = null;
        if (speechMs >= _options.MinSpeechMilliseconds)
        {
            result = new Utterance(
                _utterance.AsSpan(0, _utteranceLength).ToArray(),
                _speechOnly.AsSpan(0, _speechOnlyLength).ToArray(),
                _vad.SampleRate,
                _firstTimestamp,
                Common.Ticks.Now(),
                speechMs,
                truncated);
        }

        _inSpeech = false;
        _utteranceLength = 0;
        _speechOnlyLength = 0;
        _speechRun = 0;
        _silenceRun = 0;
        _speechChunks = 0;
        _preRollCount = 0;
        _preRollWrite = 0;
        return result;
    }

    private void AppendPreRoll(ReadOnlySpan<float> chunk)
    {
        foreach (float s in chunk)
        {
            _preRoll[_preRollWrite] = s;
            _preRollWrite = (_preRollWrite + 1) % _preRoll.Length;
        }

        _preRollCount = Math.Min(_preRoll.Length, _preRollCount + chunk.Length);
    }

    private void CopyPreRollToUtterance()
    {
        _utteranceLength = 0;
        _speechOnlyLength = 0;
        int start = (_preRollWrite - _preRollCount + _preRoll.Length) % _preRoll.Length;
        for (int i = 0; i < _preRollCount; i++)
        {
            _utterance[_utteranceLength++] = _preRoll[(start + i) % _preRoll.Length];
        }

        // The chunks that triggered the start are speech: seed the speech-only buffer with them.
        int triggered = Math.Min(_preRollCount, _speechRun * _chunk.Length);
        _utterance.AsSpan(_utteranceLength - triggered, triggered).CopyTo(_speechOnly);
        _speechOnlyLength = triggered;
        _preRollCount = 0;
        _preRollWrite = 0;
    }

    private static void Append(float[] target, ref int length, ReadOnlySpan<float> data)
    {
        int n = Math.Min(data.Length, target.Length - length);
        data[..n].CopyTo(target.AsSpan(length));
        length += n;
    }
}
