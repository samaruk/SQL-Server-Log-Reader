using BonikPOS.VoiceSearch.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BonikPOS.VoiceSearch.AI.Onnx;

/// <summary>
/// Validates a model's actual inputs/outputs (read from the ONNX graph) against what an adapter requires, so a wrong
/// or re-exported model fails loudly at load time instead of producing garbage. Dimensions: a positive value must match
/// exactly, -1 accepts any value (dynamic), null skips the check.
/// </summary>
public static class TensorContract
{
    public static NodeMetadata RequireInput(InferenceSession session, string name, TensorElementType type, params int?[] dims) =>
        Require(session.InputMetadata, "input", session, name, type, dims);

    public static NodeMetadata RequireOutput(InferenceSession session, string name, TensorElementType type, params int?[] dims) =>
        Require(session.OutputMetadata, "output", session, name, type, dims);

    /// <summary>Fails when the model has inputs the adapter does not know how to feed.</summary>
    public static void RequireNoOtherInputs(InferenceSession session, IReadOnlyCollection<string> known)
    {
        var unknown = session.InputMetadata.Keys.Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new ModelContractException($"Model has unexpected inputs: {string.Join(", ", unknown)}. {Describe(session)}");
        }
    }

    /// <summary>Static dimension of an input/output, or null when the model declares it dynamic.</summary>
    public static int? StaticDim(NodeMetadata metadata, int axis) =>
        axis < metadata.Dimensions.Length && metadata.Dimensions[axis] > 0 ? metadata.Dimensions[axis] : null;

    public static string Describe(InferenceSession session)
    {
        static string Fmt(IReadOnlyDictionary<string, NodeMetadata> d) =>
            string.Join("; ", d.Select(p => $"{p.Key}:{p.Value.ElementDataType}[{string.Join(",", p.Value.Dimensions)}]"));
        return $"Actual inputs: {Fmt(session.InputMetadata)}. Actual outputs: {Fmt(session.OutputMetadata)}.";
    }

    private static NodeMetadata Require(
        IReadOnlyDictionary<string, NodeMetadata> map, string kind, InferenceSession session, string name, TensorElementType type, int?[] dims)
    {
        if (!map.TryGetValue(name, out var meta))
        {
            throw new ModelContractException($"Model has no {kind} named '{name}'. {Describe(session)}");
        }

        if (!meta.IsTensor || meta.ElementDataType != type)
        {
            throw new ModelContractException($"{kind} '{name}' must be a {type} tensor but is {meta.ElementDataType}. {Describe(session)}");
        }

        if (dims.Length > 0)
        {
            if (meta.Dimensions.Length != dims.Length)
            {
                throw new ModelContractException(
                    $"{kind} '{name}' must have rank {dims.Length} but has rank {meta.Dimensions.Length}. {Describe(session)}");
            }

            for (int i = 0; i < dims.Length; i++)
            {
                int? expected = dims[i];
                int actual = meta.Dimensions[i];
                if (expected is > 0 && actual > 0 && actual != expected)
                {
                    throw new ModelContractException(
                        $"{kind} '{name}' dimension {i} must be {expected} but is {actual}. {Describe(session)}");
                }
            }
        }

        return meta;
    }
}
