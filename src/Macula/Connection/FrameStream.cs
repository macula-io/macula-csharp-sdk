using Macula.Frame;

namespace Macula.Connection;

/// <summary>
/// Reads and writes length-prefixed CBOR frames (<see cref="WireCodec"/>)
/// over any duplex byte stream. Backs both the control stream and any
/// dedicated stream (content transfer, streaming RPC) -- the framing is
/// identical either way, only the stream's purpose differs.
/// </summary>
public sealed class FrameStream
{
    private readonly Stream _stream;
    private byte[] _buf = new byte[4096];
    private int _len;
    private readonly byte[] _readScratch = new byte[8192];

    public FrameStream(Stream stream)
    {
        _stream = stream;
    }

    public async Task SendFrameAsync(Value frame, CancellationToken ct = default)
    {
        var bytes = WireCodec.Encode(frame);
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Blocks for the next complete frame. Throws <see cref="EndOfStreamException"/> if the stream ends first.</summary>
    public async Task<Value> RecvFrameAsync(CancellationToken ct = default)
    {
        while (true)
        {
            if (TryTakeFrame(out var frame))
            {
                return frame;
            }

            var n = await _stream.ReadAsync(_readScratch, ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("stream closed before a complete frame arrived");
            }
            Append(_readScratch.AsSpan(0, n));
        }
    }

    private bool TryTakeFrame(out Value frame)
    {
        var decoded = WireCodec.Decode(_buf.AsSpan(0, _len));
        if (decoded is Decoded.Frame f)
        {
            frame = f.Value;
            var remaining = _len - f.Consumed;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buf, f.Consumed, _buf, 0, remaining);
            }
            _len = remaining;
            return true;
        }

        frame = null!;
        return false;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(_len + data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length)
        {
            return;
        }
        var newSize = _buf.Length;
        while (newSize < needed)
        {
            newSize *= 2;
        }
        Array.Resize(ref _buf, newSize);
    }
}
