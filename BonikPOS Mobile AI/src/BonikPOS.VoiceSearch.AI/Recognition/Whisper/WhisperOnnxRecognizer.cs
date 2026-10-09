using System.Diagnostics;
using BonikPOS.VoiceSearch.AI.Dsp;
using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.Core.Recognition;
using BonikPOS.VoiceSearch.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.AI.Recognition.Whisper;

/// <summary>
/// Whisper speech recognition with ONNX Runtime (contract: docs/MODELS.md, produced by export_whisper_onnx.py).
/// Greedy decoding with a self-attention KV cache; cross-attention keys/values are computed once per utterance by the
/// encoder. Decoding rules mirror openai-whisper: non-speech tokens and task tokens suppressed, blank/EOT suppressed at
/// the first step, timestamps disabled, no-speech probability from the SOT position.
/// </summary>
public sealed class WhisperOnnxRecognizer : ISpeechRecognitionService
{
    private const double NoSpeechThreshold = 0.6;
    private const double LogProbThreshold = -1.0;
    private const int RepetitionLimit = 8;

    private readonly WhisperModelConfig _config;
    private readonly WhisperVocabulary _vocabulary;
    private readonly LoadedSession _encoder;
    private readonly LoadedSession _decoder;
    private readonly IPerformanceTelemetry? _telemetry;
    private readonly WhisperLogMel _logMel;
    private readonly float[] _mel;
    private readonly bool[] _suppress;
    private readonly int[] _suppressAtBegin;
    private readonly float[] _logits;
    private readonly RunOptions _runOptions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string[] _encoderOutputs = ["cross_k", "cross_v"];
    private readonly string[] _decoderInputs = ["tokens", "past_k", "past_v", "cross_k", "cross_v"];
    private readonly string[] _decoderOutputs = ["logits", "present_k", "present_v"];

    private WhisperOnnxRecognizer(
        WhisperModelConfig config, WhisperVocabulary vocabulary, LoadedSession encoder, LoadedSession decoder, IPerformanceTelemetry? telemetry)
    {
        _config = config;
        _vocabulary = vocabulary;
        _encoder = encoder;
        _decoder = decoder;
        _telemetry = telemetry;
        _logMel = new WhisperLogMel(config.Mels);
        _mel = new float[config.Mels * config.Frames];
        _logits = new float[config.Vocabulary];
        _suppress = new bool[config.Vocabulary];
        foreach (int t in config.SuppressTokens)
        {
            _suppress[t] = true;
        }

        for (int t = config.TimestampBegin; t < config.Vocabulary; t++)
        {
            _suppress[t] = true; // the app always decodes with <|notimestamps|>
        }

        _suppressAtBegin = [.. config.SuppressAtBegin];
        ValidateContract();
        Info = new SpeechRecognizerInfo(
            true, config.ModelId,
            $"Whisper ONNX ({config.ModelId}, {config.Quantization}, {config.AudioContext * 2 / 100.0:0.#} s window, encoder {encoder.ExecutionProvider})",
            config.Frames * WhisperLogMel.HopLength,
            config.IsRandomTestModel ? "RANDOM-WEIGHT TEST MODEL - output is meaningless" : "Ready");
    }

    public SpeechRecognizerInfo Info { get; }

    public string EncoderExecutionProvider => _encoder.ExecutionProvider;

    public double InitMilliseconds => _encoder.InitMilliseconds + _decoder.InitMilliseconds;

    public static WhisperOnnxRecognizer Load(
        ResolvedModel model, OnnxSessionPreferences preferences, IPerformanceTelemetry? telemetry = null, ILogger? logger = null)
    {
        var config = WhisperModelConfig.Load(model.PathOf("config"));
        var vocabulary = WhisperVocabulary.Load(model.PathOf("tokens"), config.Vocabulary);
        var encoder = OnnxSessionFactory.Create(model.PathOf("encoder"), preferences, allowAccelerator: true, logger);
        LoadedSession? decoder = null;
        try
        {
            decoder = OnnxSessionFactory.Create(model.PathOf("decoder"), preferences, allowAccelerator: true, logger);
            return new WhisperOnnxRecognizer(config, vocabulary, encoder, decoder, telemetry);
        }
        catch
        {
            encoder.Dispose();
            decoder?.Dispose();
            throw;
        }
    }

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<float> audio16k, SpeechRecognitionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Recognize(audio16k, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _encoder.Dispose();
        _decoder.Dispose();
        _gate.Dispose();
    }

    private SpeechRecognitionResult Recognize(ReadOnlyMemory<float> audio, SpeechRecognitionOptions options, CancellationToken token)
    {
        var d = Decode(audio.Span, options.LanguageMode, options.MaxTokens, repetitionGuard: true, token, options.AlternativeLanguageBelow);
        string text = _vocabulary.Decode(d.Tokens, _config.Eot).Trim();
        bool silence = d.NoSpeechProbability > NoSpeechThreshold && d.AverageLogProbability < LogProbThreshold;
        if (silence)
        {
            text = string.Empty; // Whisper's silence rule: likely no speech, do not trust the hypothesis.
        }

        var alternatives = new List<RecognitionAlternative>();
        if (!silence && d.Alternative is { Tokens.Count: > 0 } alt)
        {
            string altText = _vocabulary.Decode(alt.Tokens, _config.Eot).Trim();
            if (altText.Length > 0 && !string.Equals(altText, text, StringComparison.Ordinal))
            {
                alternatives.Add(new RecognitionAlternative(altText, alt.Language, Math.Clamp(Math.Exp(alt.AverageLogProbability), 0, 1)));
            }
        }

        double confidence = d.Tokens.Count > 0 ? Math.Clamp(Math.Exp(d.AverageLogProbability), 0, 1) : 0;
        _telemetry?.Record(TelemetryMetrics.AsrFeatureMs, d.FeatureTime.TotalMilliseconds);
        _telemetry?.Record(TelemetryMetrics.AsrEncoderMs, d.EncoderTime.TotalMilliseconds);
        _telemetry?.Record(TelemetryMetrics.AsrDecoderMs, d.DecoderTime.TotalMilliseconds);
        return new SpeechRecognitionResult(
            text, d.Language, confidence, d.AverageLogProbability, d.NoSpeechProbability, d.Tokens.Count,
            TimeSpan.FromSeconds((double)audio.Length / 16000), d.FeatureTime, d.EncoderTime, d.DecoderTime,
            _config.ModelId, d.Truncated)
        {
            Alternatives = alternatives,
        };
    }

    /// <summary>
    /// Token-level greedy decoding (exposed for parity tests against the PyTorch reference). In automatic Bangla/English
    /// mode, when the chosen language's probability is below <paramref name="alternativeLanguageBelow"/>, the other
    /// language is decoded too from the same encoder output and start-of-transcript cache.
    /// </summary>
    internal WhisperDecodeResult Decode(
        ReadOnlySpan<float> audio, RecognitionLanguageMode mode, int maxTokens, bool repetitionGuard, CancellationToken token,
        double alternativeLanguageBelow = 0)
    {
        var total = Stopwatch.StartNew();
        int maxSamples = _config.Frames * WhisperLogMel.HopLength;
        bool truncated = audio.Length > maxSamples;
        _logMel.Compute(audio[..Math.Min(audio.Length, maxSamples)], _config.Frames, _mel);
        var featureTime = total.Elapsed;
        token.ThrowIfCancellationRequested();

        using var melValue = OrtValue.CreateTensorValueFromMemory(_mel, [1, _config.Mels, _config.Frames]);
        using var encoded = _encoder.Session.Run(_runOptions, ["mel"], [melValue], _encoderOutputs);
        var encoderTime = total.Elapsed - featureTime;
        var crossK = encoded[0];
        var crossV = encoded[1];

        using var empty = OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance, TensorElementType.Float, [_config.TextLayers, 1, 0, _config.TextState]);

        // 1) <|startoftranscript|> alone: language logits and the no-speech probability (as whisper.decode does).
        //    Its cache is kept for a possible second-language decode.
        using var sot = Step([_config.Sot], empty, empty, crossK, crossV);
        ReadLogits(sot);
        string language = ChooseLanguage(mode);
        double noSpeech = Softmax(_logits, _config.NoSpeech);
        var languageLogits = _config.Languages.Where(p => p.Key is "bn" or "en").ToDictionary(p => p.Key, p => (double)_logits[p.Value]);

        // 2) language, task and no-timestamps tokens, then greedy decoding one token per step.
        var primary = Greedy(sot, language, crossK, crossV, maxTokens, repetitionGuard, token);
        WhisperHypothesis? alternative = null;
        if (mode == RecognitionLanguageMode.AutoBanglaEnglish && alternativeLanguageBelow > 0 && languageLogits.Count == 2
            && PairProbability(languageLogits, language) < alternativeLanguageBelow)
        {
            alternative = Greedy(sot, language == "bn" ? "en" : "bn", crossK, crossV, maxTokens, repetitionGuard, token);
        }

        var decoderTime = total.Elapsed - featureTime - encoderTime;
        return new WhisperDecodeResult(primary.Tokens, primary.LogProbabilities, language, languageLogits, noSpeech, primary.AverageLogProbability,
            truncated || primary.HitLimit, featureTime, encoderTime, decoderTime)
        {
            Alternative = alternative,
        };
    }

    /// <summary>Greedy decoding for one language from the start-of-transcript cache (which is not disposed here).</summary>
    private WhisperHypothesis Greedy(
        IDisposableReadOnlyCollection<OrtValue> sot, string language, OrtValue crossK, OrtValue crossV, int maxTokens, bool repetitionGuard, CancellationToken token)
    {
        int[] next = [_config.Languages[language], _config.Transcribe, _config.NoTimestamps];
        var tokens = new List<int>(maxTokens);
        var logProbs = new List<double>(maxTokens);
        int maxSteps = Math.Min(maxTokens, _config.TextContext / 2);
        bool ended = false;
        IDisposableReadOnlyCollection<OrtValue>? state = null;
        try
        {
            for (int step = 0; step < maxSteps; step++)
            {
                token.ThrowIfCancellationRequested();
                var source = state ?? sot;
                var current = Step(next, source[1], source[2], crossK, crossV);
                state?.Dispose(); // the previous step's cache (never the start-of-transcript cache)
                state = current;
                ReadLogits(state);
                (int best, double logProb) = Pick(step == 0);
                if (best == _config.Eot)
                {
                    ended = true;
                    break;
                }

                tokens.Add(best);
                logProbs.Add(logProb);
                if (repetitionGuard && IsRepetitionLoop(tokens))
                {
                    break;
                }

                next = [best];
            }
        }
        finally
        {
            state?.Dispose();
        }

        double avg = logProbs.Count > 0 ? logProbs.Average() : double.NegativeInfinity;
        return new WhisperHypothesis(tokens, logProbs, language, avg, !ended && tokens.Count >= maxSteps);
    }

    /// <summary>Probability of <paramref name="language"/> between the two candidate languages (softmax of two logits).</summary>
    private static double PairProbability(IReadOnlyDictionary<string, double> logits, string language)
    {
        double max = logits.Values.Max();
        double sum = logits.Values.Sum(l => Math.Exp(l - max));
        return Math.Exp(logits[language] - max) / sum;
    }

    /// <summary>
    /// Feeds a fixed token sequence through the KV-cached decoder (first four tokens at once, then one by one) and
    /// returns the five highest logits at every position from index 3 on. Used to verify the cache against the
    /// full-sequence PyTorch decoder.
    /// </summary>
    internal IReadOnlyList<(int[] Top5, float[] Top5Logits)> TeacherForce(ReadOnlySpan<float> audio, IReadOnlyList<int> sequence)
    {
        _logMel.Compute(audio[..Math.Min(audio.Length, _config.Frames * WhisperLogMel.HopLength)], _config.Frames, _mel);
        using var melValue = OrtValue.CreateTensorValueFromMemory(_mel, [1, _config.Mels, _config.Frames]);
        using var encoded = _encoder.Session.Run(_runOptions, ["mel"], [melValue], _encoderOutputs);
        using var empty = OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance, TensorElementType.Float, [_config.TextLayers, 1, 0, _config.TextState]);
        var result = new List<(int[], float[])>();
        var state = Step([.. sequence.Take(4)], empty, empty, encoded[0], encoded[1]);
        try
        {
            ReadLogits(state);
            result.Add(Top5());
            for (int i = 4; i < sequence.Count; i++)
            {
                var previous = state;
                state = Step([sequence[i]], previous[1], previous[2], encoded[0], encoded[1]);
                previous.Dispose();
                ReadLogits(state);
                result.Add(Top5());
            }
        }
        finally
        {
            state.Dispose();
        }

        return result;
    }

    private (int[] Top5, float[] Top5Logits) Top5()
    {
        var idx = Enumerable.Range(0, _logits.Length).OrderByDescending(i => _logits[i]).Take(5).ToArray();
        return (idx, [.. idx.Select(i => _logits[i])]);
    }

    private IDisposableReadOnlyCollection<OrtValue> Step(int[] tokens, OrtValue pastK, OrtValue pastV, OrtValue crossK, OrtValue crossV)
    {
        var ids = new long[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            ids[i] = tokens[i];
        }

        using var tokenValue = OrtValue.CreateTensorValueFromMemory(ids, [1, ids.Length]);
        return _decoder.Session.Run(_runOptions, _decoderInputs, [tokenValue, pastK, pastV, crossK, crossV], _decoderOutputs);
    }

    private void ReadLogits(IDisposableReadOnlyCollection<OrtValue> outputs)
    {
        var span = outputs[0].GetTensorDataAsSpan<float>();
        if (span.Length != _logits.Length)
        {
            throw new ModelContractException($"Decoder returned {span.Length} logits; expected {_logits.Length}.");
        }

        span.CopyTo(_logits);
    }

    private string ChooseLanguage(RecognitionLanguageMode mode)
    {
        bool hasBn = _config.Languages.ContainsKey("bn");
        bool hasEn = _config.Languages.ContainsKey("en");
        return mode switch
        {
            RecognitionLanguageMode.Bangla when hasBn => "bn",
            RecognitionLanguageMode.English when hasEn => "en",
            _ when hasBn && hasEn => _logits[_config.Languages["bn"]] >= _logits[_config.Languages["en"]] ? "bn" : "en",
            _ => hasEn ? "en" : _config.Languages.Keys.First(),
        };
    }

    private (int Token, double LogProb) Pick(bool firstStep)
    {
        if (firstStep)
        {
            foreach (int t in _suppressAtBegin)
            {
                _logits[t] = float.NegativeInfinity;
            }
        }

        int best = -1;
        float max = float.NegativeInfinity;
        for (int i = 0; i < _logits.Length; i++)
        {
            if (_suppress[i])
            {
                _logits[i] = float.NegativeInfinity;
                continue;
            }

            if (_logits[i] > max)
            {
                max = _logits[i];
                best = i;
            }
        }

        if (best < 0)
        {
            return (_config.Eot, double.NegativeInfinity);
        }

        double sum = 0;
        foreach (float l in _logits)
        {
            if (!float.IsNegativeInfinity(l))
            {
                sum += Math.Exp(l - max);
            }
        }

        return (best, -Math.Log(sum));
    }

    private static bool IsRepetitionLoop(List<int> tokens)
    {
        if (tokens.Count < RepetitionLimit)
        {
            return false;
        }

        int last = tokens[^1];
        for (int i = tokens.Count - RepetitionLimit; i < tokens.Count; i++)
        {
            if (tokens[i] != last)
            {
                return false;
            }
        }

        return true;
    }

    private static double Softmax(float[] logits, int index)
    {
        float max = float.NegativeInfinity;
        foreach (float l in logits)
        {
            max = Math.Max(max, l);
        }

        double sum = 0;
        foreach (float l in logits)
        {
            sum += Math.Exp(l - max);
        }

        return Math.Exp(logits[index] - max) / sum;
    }

    private void ValidateContract()
    {
        var c = _config;
        var e = _encoder.Session;
        TensorContract.RequireInput(e, "mel", TensorElementType.Float, 1, c.Mels, c.Frames);
        TensorContract.RequireNoOtherInputs(e, ["mel"]);
        TensorContract.RequireOutput(e, "cross_k", TensorElementType.Float, c.TextLayers, 1, c.AudioContext, c.TextState);
        TensorContract.RequireOutput(e, "cross_v", TensorElementType.Float, c.TextLayers, 1, c.AudioContext, c.TextState);
        var d = _decoder.Session;
        TensorContract.RequireInput(d, "tokens", TensorElementType.Int64, 1, -1);
        TensorContract.RequireInput(d, "past_k", TensorElementType.Float, c.TextLayers, 1, -1, c.TextState);
        TensorContract.RequireInput(d, "past_v", TensorElementType.Float, c.TextLayers, 1, -1, c.TextState);
        TensorContract.RequireInput(d, "cross_k", TensorElementType.Float, c.TextLayers, 1, c.AudioContext, c.TextState);
        TensorContract.RequireInput(d, "cross_v", TensorElementType.Float, c.TextLayers, 1, c.AudioContext, c.TextState);
        TensorContract.RequireNoOtherInputs(d, _decoderInputs);
        TensorContract.RequireOutput(d, "logits", TensorElementType.Float, 1, c.Vocabulary);
        TensorContract.RequireOutput(d, "present_k", TensorElementType.Float, c.TextLayers, 1, -1, c.TextState);
        TensorContract.RequireOutput(d, "present_v", TensorElementType.Float, c.TextLayers, 1, -1, c.TextState);
    }
}

internal sealed record WhisperDecodeResult(
    IReadOnlyList<int> Tokens,
    IReadOnlyList<double> LogProbabilities,
    string Language,
    IReadOnlyDictionary<string, double> LanguageLogits,
    double NoSpeechProbability,
    double AverageLogProbability,
    bool Truncated,
    TimeSpan FeatureTime,
    TimeSpan EncoderTime,
    TimeSpan DecoderTime)
{
    /// <summary>Second-language hypothesis when language detection was uncertain.</summary>
    public WhisperHypothesis? Alternative { get; init; }
}

internal sealed record WhisperHypothesis(
    IReadOnlyList<int> Tokens, IReadOnlyList<double> LogProbabilities, string Language, double AverageLogProbability, bool HitLimit);
