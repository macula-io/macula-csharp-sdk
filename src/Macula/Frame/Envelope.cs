using Macula.Cbor;
using Macula.Identity;

namespace Macula.Frame;

/// <summary>
/// The macula application-frame envelope: construction, Ed25519
/// signing/verification. Ported from
/// <c>src/peering/macula_frame.erl</c> (macula-io/macula) via
/// macula-rust-sdk's own <c>src/frame.rs</c>.
///
/// Every frame carries a common envelope -- version, frame_type, frame_id
/// (UUIDv7), sent_at_ms, capabilities, plus realm/call_id/source_route set
/// to null unless the specific frame type populates them -- and every
/// frame is Ed25519-signed over its own canonical bytes with
/// signature/publisher_sig stripped first.
/// </summary>
public static class Envelope
{
    /// <summary>
    /// Domain separator for the per-frame Ed25519 signature. Distinct from
    /// the SWIM-update and publisher end-to-end domains, neither of which
    /// this SDK implements yet (matching the sibling Go/Rust SDKs).
    /// </summary>
    public static readonly byte[] SigDomain = "macula-v2-frame\0"u8.ToArray();

    public const long ProtocolVersion = 2;

    /// <summary>16 MiB minus one byte -- matches `?MAX_FRAME_BYTES` (`16#FFFFFF`) exactly.</summary>
    public const int MaxFrameBytes = 0x00FF_FFFF;

    public static ulong CurrentMillis() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>A fresh UUIDv7, as its raw 16 big-endian bytes (RFC 9562 layout).</summary>
    public static byte[] FreshFrameId() => Guid.CreateVersion7().ToByteArray(bigEndian: true);

    /// <summary>
    /// The common envelope every frame carries, matching `base/2`. Field
    /// order doesn't matter -- canonical CBOR re-sorts by encoded key bytes
    /// at encode time regardless.
    /// </summary>
    public static Value.MapValue Base(string frameType, ulong capabilities, byte[] frameId, ulong sentAtMs) =>
        (Value.MapValue)Value.Map(new List<KeyValuePair<Value, Value>>
        {
            new(Value.Text("version"), Value.Int(ProtocolVersion)),
            new(Value.Text("frame_type"), Value.Text(frameType)),
            new(Value.Text("frame_id"), Value.Bytes(frameId)),
            new(Value.Text("sent_at_ms"), Value.UInt(sentAtMs)),
            new(Value.Text("capabilities"), Value.UInt(capabilities)),
            new(Value.Text("realm"), Value.Null),
            new(Value.Text("call_id"), Value.Null),
            new(Value.Text("source_route"), Value.Null),
        });

    public static Value Bytes32List(IReadOnlyList<byte[]> items) =>
        Value.List(items.Select(b => Value.Bytes(b)).ToList());

    /// <summary>
    /// Sign <paramref name="frame"/> with <paramref name="identity"/>, over
    /// SigDomain || canonical_cbor(frame minus signature/publisher_sig),
    /// returning the frame with its `signature` field set (64 bytes).
    /// </summary>
    public static Value.MapValue Sign(Value.MapValue frame, KeyPair identity)
    {
        var signable = SignableBytes(frame);
        var sig = identity.Sign(signable);
        return frame.WithField("signature", Value.Bytes(sig));
    }

    private static byte[] SignableBytes(Value.MapValue frame)
    {
        var unsigned = frame.Without("signature", "publisher_sig");
        var canonical = CborCodec.Encode(unsigned);
        var outBuf = new byte[SigDomain.Length + canonical.Length];
        SigDomain.CopyTo(outBuf, 0);
        canonical.CopyTo(outBuf, SigDomain.Length);
        return outBuf;
    }

    public enum VerifyError
    {
        MissingSignature,
        BadSignature,
        SignatureInvalid,
    }

    /// <summary>
    /// Verify <paramref name="frame"/>'s `signature` field against
    /// <paramref name="pubkey"/>, over the same domain-separated bytes
    /// <see cref="Sign"/> produces. Returns null on success, the failure
    /// reason otherwise.
    /// </summary>
    public static VerifyError? Verify(Value.MapValue frame, byte[] pubkey)
    {
        if (frame.Get("signature") is not Value.BytesValue sigField)
        {
            return VerifyError.MissingSignature;
        }
        if (sigField.Value.Length != 64)
        {
            return VerifyError.BadSignature;
        }

        var signable = SignableBytes(frame);
        return KeyPair.Verify(signable, sigField.Value, pubkey) ? null : VerifyError.SignatureInvalid;
    }
}
