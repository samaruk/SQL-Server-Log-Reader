using BonikPOS.VoiceSearch.AI.Onnx;
using BonikPOS.VoiceSearch.Core.Models;
using BonikPOS.VoiceSearch.UnitTests.TestSupport;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.UnitTests.AI;

public sealed class TensorContractTests(GeneratedModels models) : IClassFixture<GeneratedModels>, IDisposable
{
    private readonly InferenceSession _session = new(models.Contract);

    public void Dispose() => _session.Dispose();

    [Fact]
    public void MatchingDeclarations_Pass_AndDynamicOrSkippedDimensionsAcceptAnything()
    {
        Assert.Equal([1, 512], TensorContract.RequireInput(_session, "audio", TensorElementType.Float, 1, 512).Dimensions);
        TensorContract.RequireInput(_session, "audio", TensorElementType.Float, -1, -1);
        TensorContract.RequireInput(_session, "audio", TensorElementType.Float, null, 512);
        TensorContract.RequireInput(_session, "audio", TensorElementType.Float); // no shape check at all
        TensorContract.RequireInput(_session, "state", TensorElementType.Float, 2, 1, 128); // "B" is symbolic: any value
        TensorContract.RequireInput(_session, "lengths", TensorElementType.Int64, 7);
        TensorContract.RequireOutput(_session, "audio_out", TensorElementType.Float, 1, 512);
        TensorContract.RequireNoOtherInputs(_session, ["audio", "lengths", "state"]);
    }

    [Fact]
    public void WrongName_IsRejectedWithTheActualSignatureInTheMessage()
    {
        var ex = Assert.Throws<ModelContractException>(() => TensorContract.RequireInput(_session, "mixture", TensorElementType.Float));
        Assert.Contains("no input named 'mixture'", ex.Message);
        Assert.Contains("Actual inputs: audio:Float[1,512]", ex.Message);
        Assert.Contains("lengths:Int64[-1]", ex.Message);

        // An output is not an input and vice versa.
        Assert.Contains("no output named 'audio'",
            Assert.Throws<ModelContractException>(() => TensorContract.RequireOutput(_session, "audio", TensorElementType.Float)).Message);
        Assert.Contains("no input named 'audio_out'",
            Assert.Throws<ModelContractException>(() => TensorContract.RequireInput(_session, "audio_out", TensorElementType.Float)).Message);
    }

    [Fact]
    public void WrongElementType_IsRejected()
    {
        var ex = Assert.Throws<ModelContractException>(() => TensorContract.RequireInput(_session, "lengths", TensorElementType.Float));
        Assert.Contains("input 'lengths' must be a Float tensor but is Int64", ex.Message);

        ex = Assert.Throws<ModelContractException>(() => TensorContract.RequireOutput(_session, "audio_out", TensorElementType.Int64));
        Assert.Contains("output 'audio_out' must be a Int64 tensor but is Float", ex.Message);
    }

    [Fact]
    public void WrongStaticDimension_IsRejected()
    {
        var ex = Assert.Throws<ModelContractException>(() => TensorContract.RequireInput(_session, "audio", TensorElementType.Float, 1, 320));
        Assert.Contains("input 'audio' dimension 1 must be 320 but is 512", ex.Message);

        ex = Assert.Throws<ModelContractException>(() => TensorContract.RequireInput(_session, "state", TensorElementType.Float, 2, -1, 64));
        Assert.Contains("dimension 2 must be 64 but is 128", ex.Message);

        ex = Assert.Throws<ModelContractException>(() => TensorContract.RequireOutput(_session, "audio_out", TensorElementType.Float, 2, 512));
        Assert.Contains("output 'audio_out' dimension 0 must be 2 but is 1", ex.Message);
    }

    [Fact]
    public void WrongRank_IsRejected()
    {
        var ex = Assert.Throws<ModelContractException>(() => TensorContract.RequireInput(_session, "audio", TensorElementType.Float, 1, 512, 1));
        Assert.Contains("must have rank 3 but has rank 2", ex.Message);
    }

    [Fact]
    public void UnknownExtraInputs_AreRejected()
    {
        var ex = Assert.Throws<ModelContractException>(() => TensorContract.RequireNoOtherInputs(_session, ["audio", "lengths"]));
        Assert.Contains("unexpected inputs: state", ex.Message);
    }

    [Fact]
    public void StaticDim_ReportsOnlyPositiveDeclaredDimensions()
    {
        var audio = _session.InputMetadata["audio"];
        var state = _session.InputMetadata["state"];
        Assert.Equal(512, TensorContract.StaticDim(audio, 1));
        Assert.Null(TensorContract.StaticDim(state, 1)); // symbolic
        Assert.Null(TensorContract.StaticDim(audio, 5)); // axis beyond the rank
    }
}
