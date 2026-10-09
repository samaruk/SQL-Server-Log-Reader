using System.Buffers.Binary;
using System.Text;

namespace BonikPOS.VoiceSearch.Audio;

/// <summary>Decoded PCM audio. Samples are interleaved float in [-1, 1].</summary>
public sealed record PcmAudio(float[] Samples, int SampleRate, int Channels)
{
    public int Frames => Samples.Length / Channels;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    /// <summary>Channel 0 as a mono array.</summary>
    public float[] ToMono()
    {
        if (Channels == 1)
        {
            return Samples;
        }

        var mono = new float[Frames];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = Samples[i * Channels];
        }

        return mono;
    }
}

/// <summary>Minimal RIFF/WAVE reader and writer (PCM 16/24/32-bit integer and IEEE float 32-bit).</summary>
public static class WavFile
{
    public static PcmAudio Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static PcmAudio Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = new(reader.ReadChars(4));
            int size = reader.ReadInt32();
            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (size > 16)
                {
                    var extra = reader.ReadBytes(size - 16);
                    if (format == 0xFFFE && extra.Length >= 10)
                    {
                        format = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(8));
                    }
                }
            }
            else if (id == "data")
            {
                data = reader.ReadBytes(size);
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
                continue;
            }

            if ((size & 1) == 1 && stream.Position < stream.Length)
            {
                stream.Seek(1, SeekOrigin.Current);
            }
        }

        if (data is null || channels == 0 || rate == 0)
        {
            throw new InvalidDataException("WAVE file has no fmt or data chunk.");
        }

        return new PcmAudio(Decode(data, format, bits), rate, channels);
    }

    /// <summary>Writes 16-bit PCM.</summary>
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        using var stream = File.Create(path);
        Write(stream, samples, sampleRate, channels);
    }

    public static void Write(Stream stream, ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        int dataBytes = samples.Length * 2;
        Span<byte> header = stackalloc byte[44];
        Encoding.ASCII.GetBytes("RIFF", header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVEfmt ", header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        Encoding.ASCII.GetBytes("data", header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        stream.Write(header);
        var buffer = new byte[Math.Min(dataBytes, 1 << 16)];
        int i = 0;
        while (i < samples.Length)
        {
            int n = Math.Min(buffer.Length / 2, samples.Length - i);
            for (int k = 0; k < n; k++)
            {
                float s = Math.Clamp(samples[i + k], -1f, 1f);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(k * 2), (short)Math.Round(s * 32767f));
            }

            stream.Write(buffer, 0, n * 2);
            i += n;
        }
    }

    private static float[] Decode(byte[] data, int format, int bits)
    {
        switch (format, bits)
        {
            case (1, 16):
            {
                var s = new float[data.Length / 2];
                for (int i = 0; i < s.Length; i++)
                {
                    s[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2)) / 32768f;
                }

                return s;
            }

            case (1, 24):
            {
                var s = new float[data.Length / 3];
                for (int i = 0; i < s.Length; i++)
                {
                    int v = data[i * 3] | (data[i * 3 + 1] << 8) | ((sbyte)data[i * 3 + 2] << 16);
                    s[i] = v / 8388608f;
                }

                return s;
            }

            case (1, 32):
            {
                var s = new float[data.Length / 4];
                for (int i = 0; i < s.Length; i++)
                {
                    s[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4)) / 2147483648f;
                }

                return s;
            }

            case (3, 32):
            {
                var s = new float[data.Length / 4];
                for (int i = 0; i < s.Length; i++)
                {
                    s[i] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i * 4));
                }

                return s;
            }

            default:
                throw new NotSupportedException($"Unsupported WAVE encoding: format {format}, {bits} bits.");
        }
    }
}
