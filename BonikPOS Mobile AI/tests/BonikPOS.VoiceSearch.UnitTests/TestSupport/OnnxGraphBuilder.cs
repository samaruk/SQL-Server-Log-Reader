using System.Text;

namespace BonikPOS.VoiceSearch.UnitTests.TestSupport;

/// <summary>ONNX TensorProto.DataType values used by the tests.</summary>
public enum OnnxType
{
    Float = 1,
    Int32 = 6,
    Int64 = 7,
    Double = 11,
}

/// <summary>
/// Writes tiny ONNX models (protobuf, IR 8, opset 17) so unit tests can exercise tensor-contract validation and
/// streaming adapters against graphs with exactly the names, element types and shapes a test needs - no Python
/// toolchain and no fixture files. Only the handful of protobuf fields the tests need are implemented.
/// A dimension is a positive integer (static) or a string (symbolic, reported by ONNX Runtime as -1).
/// </summary>
public sealed class OnnxGraphBuilder
{
    private readonly List<byte[]> _inputs = [];
    private readonly List<byte[]> _outputs = [];
    private readonly List<byte[]> _nodes = [];
    private int _nodeCount;

    public OnnxGraphBuilder Input(string name, OnnxType type, params object[] dims)
    {
        _inputs.Add(ValueInfo(name, type, dims));
        return this;
    }

    public OnnxGraphBuilder Output(string name, OnnxType type, params object[] dims)
    {
        _outputs.Add(ValueInfo(name, type, dims));
        return this;
    }

    /// <summary>Adds a node without attributes, e.g. Identity, Add, Mul.</summary>
    public OnnxGraphBuilder Node(string opType, string[] inputs, string[] outputs)
    {
        var w = new ProtoWriter();
        foreach (var i in inputs)
        {
            w.String(1, i);
        }

        foreach (var o in outputs)
        {
            w.String(2, o);
        }

        w.String(3, $"n{_nodeCount++}");
        w.String(4, opType);
        _nodes.Add(w.ToArray());
        return this;
    }

    public OnnxGraphBuilder Identity(string input, string output) => Node("Identity", [input], [output]);

    /// <summary>ReduceMean (opset 17: axes is an attribute).</summary>
    public OnnxGraphBuilder ReduceMean(string input, string output, long[] axes, bool keepDims)
    {
        var axesAttr = new ProtoWriter();
        axesAttr.String(1, "axes");
        foreach (long a in axes)
        {
            axesAttr.Varint(8, a);
        }

        axesAttr.Varint(20, 7); // AttributeType.INTS
        var keep = new ProtoWriter();
        keep.String(1, "keepdims");
        keep.Varint(3, keepDims ? 1 : 0);
        keep.Varint(20, 2); // AttributeType.INT

        var w = new ProtoWriter();
        w.String(1, input);
        w.String(2, output);
        w.String(3, $"n{_nodeCount++}");
        w.String(4, "ReduceMean");
        w.Message(5, axesAttr.ToArray());
        w.Message(5, keep.ToArray());
        _nodes.Add(w.ToArray());
        return this;
    }

    public byte[] Build()
    {
        var graph = new ProtoWriter();
        foreach (var n in _nodes)
        {
            graph.Message(1, n);
        }

        graph.String(2, "unit_test_graph");
        foreach (var i in _inputs)
        {
            graph.Message(11, i);
        }

        foreach (var o in _outputs)
        {
            graph.Message(12, o);
        }

        var opset = new ProtoWriter();
        opset.String(1, string.Empty);
        opset.Varint(2, 17);

        var model = new ProtoWriter();
        model.Varint(1, 8); // ir_version
        model.String(2, "bonikpos-unit-tests");
        model.Message(7, graph.ToArray());
        model.Message(8, opset.ToArray());
        return model.ToArray();
    }

    public string Save(string path)
    {
        File.WriteAllBytes(path, Build());
        return path;
    }

    private static byte[] ValueInfo(string name, OnnxType type, object[] dims)
    {
        var shape = new ProtoWriter();
        foreach (var d in dims)
        {
            var dim = new ProtoWriter();
            switch (d)
            {
                case int v:
                    dim.Varint(1, v);
                    break;
                case long v:
                    dim.Varint(1, v);
                    break;
                case string s:
                    dim.String(2, s);
                    break;
                default:
                    throw new ArgumentException($"Unsupported dimension {d}.");
            }

            shape.Message(1, dim.ToArray());
        }

        var tensor = new ProtoWriter();
        tensor.Varint(1, (int)type);
        tensor.Message(2, shape.ToArray());
        var typeProto = new ProtoWriter();
        typeProto.Message(1, tensor.ToArray());
        var info = new ProtoWriter();
        info.String(1, name);
        info.Message(2, typeProto.ToArray());
        return info.ToArray();
    }

    private sealed class ProtoWriter
    {
        private readonly MemoryStream _stream = new();

        public void Varint(int field, long value)
        {
            Tag(field, 0);
            Raw((ulong)value);
        }

        public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public void Message(int field, byte[] message) => Bytes(field, message);

        public byte[] ToArray() => _stream.ToArray();

        private void Bytes(int field, byte[] bytes)
        {
            Tag(field, 2);
            Raw((ulong)bytes.Length);
            _stream.Write(bytes);
        }

        private void Tag(int field, int wireType) => Raw((ulong)((field << 3) | wireType));

        private void Raw(ulong value)
        {
            while (value >= 0x80)
            {
                _stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            _stream.WriteByte((byte)value);
        }
    }
}
