namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>
/// Tiny ONNX graphs written once per test class (xunit class fixture). They are arithmetic stand-ins with exact tensor
/// names, element types and shapes; none of them is a speech model.
/// </summary>
public sealed class GeneratedModels : IDisposable
{
    private readonly TempDirectory _dir = new();

    public GeneratedModels()
    {
        // Mixed static / symbolic dimensions and element types for TensorContract.
        Contract = new OnnxGraphBuilder()
            .Input("audio", OnnxType.Float, 1, 512)
            .Input("lengths", OnnxType.Int64, "N")
            .Input("state", OnnxType.Float, 2, "B", 128)
            .Identity("audio", "audio_out")
            .Identity("lengths", "lengths_out")
            .Identity("state", "state_out")
            .Output("audio_out", OnnxType.Float, 1, 512)
            .Output("lengths_out", OnnxType.Int64, "N")
            .Output("state_out", OnnxType.Float, 2, "B", 128)
            .Save(_dir.File("contract.onnx"));

        // Streaming TSE layout (chunk 320, 256-dim embedding, 8-float state); "enhanced" is the unchanged mixture.
        Tse = TseGraph(320, 8).Save(_dir.File("tse-320.onnx"));

        // Same, but the audio width and the state are symbolic.
        TseDynamic = TseGraph("T", "S").Save(_dir.File("tse-dynamic.onnx"));

        // "fbank-speaker-onnx" contract: feats [1, T, 80] -> embs [1, 80] (time average). Not a speaker model.
        FbankEncoder = new OnnxGraphBuilder()
            .Input("feats", OnnxType.Float, 1, "T", 80)
            .ReduceMean("feats", "embs", [1], keepDims: false)
            .Output("embs", OnnxType.Float, 1, 80)
            .Save(_dir.File("fbank-encoder.onnx"));

        NotAModel = _dir.File("not-a-model.onnx");
        System.IO.File.WriteAllText(NotAModel, "this is not protobuf");
    }

    public string Directory => _dir.Path;

    public string Contract { get; }

    public string Tse { get; }

    public string TseDynamic { get; }

    public string FbankEncoder { get; }

    public string NotAModel { get; }

    public string File(string name) => _dir.File(name);

    public void Dispose() => _dir.Dispose();

    private static OnnxGraphBuilder TseGraph(object width, object stateWidth) => new OnnxGraphBuilder()
        .Input("mixture", OnnxType.Float, 1, width)
        .Input("spk_emb", OnnxType.Float, 1, 256)
        .Input("state_in", OnnxType.Float, 1, stateWidth)
        .Identity("mixture", "enhanced")
        .Identity("state_in", "state_out")
        .Identity("spk_emb", "emb_echo")
        .Output("enhanced", OnnxType.Float, 1, width)
        .Output("state_out", OnnxType.Float, 1, stateWidth)
        .Output("emb_echo", OnnxType.Float, 1, 256);
}
