using Macula.Cbor;
using Macula.Identity;

namespace Macula.Frame;

/// <summary>
/// The macula application-frame envelope: construction, Ed25519
/// signing/verification. Ported from
/// <c>src/peering/macula_frame.erl</c> (macula-io/macula) via
/// macula-rust's own <c>src/frame.rs</c>.
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

    // -----------------------------------------------------------------
    // publisher_sig: the separate end-to-end signature on PUBLISH/EVENT
    // frames. Sign/Verify above cover a frame's own per-hop `signature`,
    // which is checked against whichever connection the frame arrived
    // on -- correct for the frame's origin (hop 1), but wrong for any
    // further relay hop, since a relayed frame's signature still
    // belongs to the ORIGINAL sender, not whichever station forwarded
    // it. publisher_sig covers just (topic, realm, publisher, seq,
    // payload), independent of frame type, so it survives PUBLISH ->
    // EVENT conversion and every relay hop. Ported from the Erlang
    // reference (macula_frame.erl:sign_publisher/2,
    // ?EVENT_PUBLISHER_DOMAIN) and checked byte-for-byte against a
    // signature generated live from that same code.
    // -----------------------------------------------------------------

    public static readonly byte[] EventPublisherDomain = "macula-v2-event-pub\0"u8.ToArray();

    /// <summary>
    /// Add `publisher_sig` to a PUBLISH or EVENT frame: <paramref
    /// name="identity"/>'s Ed25519 signature over (topic, realm,
    /// publisher, seq, payload). <paramref name="identity"/> must be the
    /// key pair for the pubkey already in the frame's `publisher` field
    /// -- not checked here (callers build frames with their own
    /// identity's pubkey as `publisher` by construction).
    /// </summary>
    public static Value.MapValue SignPublisher(Value.MapValue frame, KeyPair identity)
    {
        var signable = PublisherSigningBytes(frame);
        var sig = identity.Sign(signable);
        return frame.WithField("publisher_sig", Value.Bytes(sig));
    }

    public enum VerifyPublisherError
    {
        MissingPublisherSig,
        BadPublisherSig,
        PublisherSigInvalid,
    }

    /// <summary>
    /// Verify <paramref name="frame"/>'s `publisher_sig` against its OWN
    /// `publisher` field -- unlike <see cref="Verify"/> (the per-hop
    /// signature), there is no separate pubkey parameter: publisher_sig's
    /// whole point is proving "the pubkey named in this frame produced
    /// it", independent of which connection it arrived on. Returns null
    /// on success, the failure reason otherwise.
    /// </summary>
    public static VerifyPublisherError? VerifyPublisher(Value.MapValue frame)
    {
        if (frame.Get("publisher_sig") is not Value.BytesValue sigField)
        {
            return VerifyPublisherError.MissingPublisherSig;
        }
        if (sigField.Value.Length != 64)
        {
            return VerifyPublisherError.BadPublisherSig;
        }
        if (frame.Get("publisher") is not Value.BytesValue pubField || pubField.Value.Length != 32)
        {
            return VerifyPublisherError.BadPublisherSig;
        }

        var signable = PublisherSigningBytes(frame);
        return KeyPair.Verify(signable, sigField.Value, pubField.Value)
            ? null
            : VerifyPublisherError.PublisherSigInvalid;
    }

    /// <summary>
    /// The canonical bytes a publisher signs: a fixed 5-field tuple,
    /// independent of frame type, header fields, `delivered_via`, or
    /// `ttl_ms`, so the same signature is valid on the PUBLISH the
    /// publisher sent and on every EVENT a relay derives from it.
    /// </summary>
    private static byte[] PublisherSigningBytes(Value.MapValue frame)
    {
        string[] fields = ["topic", "realm", "publisher", "seq", "payload"];
        var pairs = fields
            .Select(f => new KeyValuePair<Value, Value>(Value.Text(f), frame.Get(f) ?? Value.Null))
            .ToList();
        var canonical = CborCodec.Encode((Value.MapValue)Value.Map(pairs));
        var outBuf = new byte[EventPublisherDomain.Length + canonical.Length];
        EventPublisherDomain.CopyTo(outBuf, 0);
        canonical.CopyTo(outBuf, EventPublisherDomain.Length);
        return outBuf;
    }
}
