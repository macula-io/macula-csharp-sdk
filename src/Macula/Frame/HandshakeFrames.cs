namespace Macula.Frame;

/// <summary>Fields for a CONNECT frame.</summary>
public sealed class ConnectSpec
{
    public required byte[] NodeId { get; init; }
    public required byte[] StationId { get; init; }
    public IReadOnlyList<byte[]> Realms { get; init; } = Array.Empty<byte[]>();
    public ulong Capabilities { get; init; }
    public required byte[] PuzzleEvidence { get; init; }
    public IReadOnlyList<Value> Addresses { get; init; } = Array.Empty<Value>();
    public Value? Site { get; init; }
    public IReadOnlyList<Value> Endorsements { get; init; } = Array.Empty<Value>();

    /// <summary>
    /// A CONNECT with no realm memberships claimed and no advertised
    /// addresses -- the shape a dial-out-only leaf client uses.
    /// </summary>
    public static ConnectSpec New(byte[] nodeId, byte[] puzzleEvidence) => new()
    {
        NodeId = nodeId,
        // `send_connect/2`'s own convention: a plain peer/daemon dial sets
        // station_id equal to node_id.
        StationId = nodeId,
        PuzzleEvidence = puzzleEvidence,
    };
}

public static class ConnectFrame
{
    public static Value.MapValue Build(ConnectSpec spec, byte[] frameId, ulong sentAtMs)
    {
        var fields = Envelope.Base("connect", spec.Capabilities, frameId, sentAtMs);
        return fields
            .WithField("node_id", Value.Bytes(spec.NodeId))
            .WithField("station_id", Value.Bytes(spec.StationId))
            .WithField("realms", Envelope.Bytes32List(spec.Realms))
            .WithField("addresses", Value.List(spec.Addresses))
            .WithField("site", spec.Site ?? Value.Null)
            .WithField("puzzle_evidence", Value.Bytes(spec.PuzzleEvidence))
            .WithField("endorsements", Value.List(spec.Endorsements));
    }

    /// <summary>Build a CONNECT frame with a fresh frame_id/sent_at_ms. Unsigned -- pass to <see cref="Envelope.Sign"/>.</summary>
    public static Value.MapValue Build(ConnectSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

public static class GoodbyeFrame
{
    public static Value.MapValue Build(string reason, string? detail, byte[] frameId, ulong sentAtMs)
    {
        var fields = Envelope.Base("goodbye", 0, frameId, sentAtMs);
        // `reason` is an Erlang atom() -> text (major 3). `detail` is
        // `binary() | undefined` -> a raw byte string (major 2), NOT text.
        return fields
            .WithField("reason", Value.Text(reason))
            .WithField("detail", detail is null ? Value.Null : Value.Bytes(System.Text.Encoding.UTF8.GetBytes(detail)));
    }

    public static Value.MapValue Build(string reason, string? detail = null) =>
        Build(reason, detail, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

/// <summary>The fields of a HELLO frame needed to drive the handshake state machine.</summary>
public sealed record HelloInfo(
    byte[] NodeId,
    byte[] StationId,
    IReadOnlyList<byte[]> Realms,
    ulong Capabilities,
    bool Accepted,
    ulong NegotiatedCapabilities,
    long? RefusalCode);

public enum ParseHelloError
{
    NotAHelloFrame,
    MissingField,
    WrongFieldType,
}

public sealed class ParseHelloException : Exception
{
    public ParseHelloError Kind { get; }
    public string? Field { get; }

    public ParseHelloException(ParseHelloError kind, string? field = null)
        : base(field is null ? kind.ToString() : $"{kind}: {field}")
    {
        Kind = kind;
        Field = field;
    }
}

public static class HelloFrame
{
    private static byte[] GetBytes32(Value.MapValue frame, string field)
    {
        return frame.Get(field) switch
        {
            Value.BytesValue { Value.Length: 32 } b => b.Value,
            null => throw new ParseHelloException(ParseHelloError.MissingField, field),
            _ => throw new ParseHelloException(ParseHelloError.WrongFieldType, field),
        };
    }

    private static IReadOnlyList<byte[]> GetBytes32List(Value.MapValue frame, string field)
    {
        if (frame.Get(field) is not Value.ListValue list)
        {
            throw frame.Get(field) is null
                ? new ParseHelloException(ParseHelloError.MissingField, field)
                : new ParseHelloException(ParseHelloError.WrongFieldType, field);
        }
        return list.Items.Select(v => v switch
        {
            Value.BytesValue { Value.Length: 32 } b => b.Value,
            _ => throw new ParseHelloException(ParseHelloError.WrongFieldType, field),
        }).ToList();
    }

    private static ulong GetUInt(Value.MapValue frame, string field)
    {
        return frame.Get(field) switch
        {
            Value.UIntValue u => u.Value,
            null => throw new ParseHelloException(ParseHelloError.MissingField, field),
            _ => throw new ParseHelloException(ParseHelloError.WrongFieldType, field),
        };
    }

    // `accepted` is an Erlang atom (true/false) -> CBOR text "true"/"false"
    // on the wire, NOT a real CBOR boolean -- major 7 has no boolean
    // support in this codec at all (see CborCodec).
    private static bool GetBool(Value.MapValue frame, string field)
    {
        return frame.Get(field) switch
        {
            Value.TextValue t when t.AsText() == "true" => true,
            Value.TextValue t when t.AsText() == "false" => false,
            null => throw new ParseHelloException(ParseHelloError.MissingField, field),
            _ => throw new ParseHelloException(ParseHelloError.WrongFieldType, field),
        };
    }

    /// <summary>Parse a decoded frame as a HELLO, checking frame_type first.</summary>
    public static HelloInfo Parse(Value frame)
    {
        if (frame is not Value.MapValue map || map.Get("frame_type") is not Value.TextValue { } ft || ft.AsText() != "hello")
        {
            throw new ParseHelloException(ParseHelloError.NotAHelloFrame);
        }

        long? refusalCode = map.Get("refusal_code") switch
        {
            null or Value.NullValue => null,
            Value.UIntValue u => (long)u.Value,
            Value.NegIntValue n => -1L - (long)n.NMinusOne,
            _ => throw new ParseHelloException(ParseHelloError.WrongFieldType, "refusal_code"),
        };

        return new HelloInfo(
            NodeId: GetBytes32(map, "node_id"),
            StationId: GetBytes32(map, "station_id"),
            Realms: GetBytes32List(map, "realms"),
            Capabilities: GetUInt(map, "capabilities"),
            Accepted: GetBool(map, "accepted"),
            NegotiatedCapabilities: GetUInt(map, "negotiated_capabilities"),
            RefusalCode: refusalCode);
    }
}
