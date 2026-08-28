using System.Buffers.Binary;
using Macula.Cbor;

namespace Macula.Frame;

public sealed class FrameTooLargeException : Exception
{
    public FrameTooLargeException(int size)
        : base($"frame is {size} bytes, exceeding the {Envelope.MaxFrameBytes}-byte cap") { }
}

/// <summary>Result of attempting to decode one frame from the head of a buffer.</summary>
public abstract record Decoded
{
    private Decoded() { }

    /// <summary>A complete frame was decoded, consuming this many bytes from the front of the buffer.</summary>
    public sealed record Frame(Value Value, int Consumed) : Decoded;

    /// <summary>The buffer doesn't yet hold a complete frame; at least this many more bytes are needed.</summary>
    public sealed record More(int NeededBytes) : Decoded;
}

/// <summary>The length-prefixed CBOR wire codec: `&lt;&lt;Length:32/big, Cbor/binary&gt;&gt;`.</summary>
public static class WireCodec
{
    public static byte[] Encode(Value frame)
    {
        var payload = CborCodec.Encode(frame);
        if (payload.Length > Envelope.MaxFrameBytes)
        {
            throw new FrameTooLargeException(payload.Length);
        }

        var outBuf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(outBuf, (uint)payload.Length);
        payload.CopyTo(outBuf, 4);
        return outBuf;
    }

    /// <summary>Decode one length-prefixed frame from the head of <paramref name="buf"/>.</summary>
    public static Decoded Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 4)
        {
            return new Decoded.More(4 - buf.Length);
        }

        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(buf);
        if (len > Envelope.MaxFrameBytes)
        {
            throw new FrameTooLargeException(len);
        }
        if (buf.Length < 4 + len)
        {
            return new Decoded.More(4 + len - buf.Length);
        }

        var value = CborCodec.Decode(buf.Slice(4, len));
        return new Decoded.Frame(value, 4 + len);
    }
}
