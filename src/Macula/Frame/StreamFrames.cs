using System.Text;

namespace Macula.Frame;

/// <summary>`mode` on a STREAM_OPEN -- who's expected to push data.</summary>
public enum StreamMode
{
    /// <summary>The provider pushes chunks at the caller.</summary>
    ServerStream,
    /// <summary>The caller pushes chunks at the provider (the content-transfer push-upload path is exactly this mode).</summary>
    ClientStream,
    /// <summary>Both directions.</summary>
    Bidi,
}

public static class StreamModeNames
{
    public static string Name(this StreamMode mode) => mode switch
    {
        StreamMode.ServerStream => "server_stream",
        StreamMode.ClientStream => "client_stream",
        StreamMode.Bidi => "bidi",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static StreamMode? FromName(string name) => name switch
    {
        "server_stream" => StreamMode.ServerStream,
        "client_stream" => StreamMode.ClientStream,
        "bidi" => StreamMode.Bidi,
        _ => null,
    };
}

/// <summary>`encoding` on a STREAM_DATA -- a hint for how to interpret `body`, not a second wire codec.</summary>
public enum StreamEncoding
{
    /// <summary>`body` is opaque bytes.</summary>
    Raw,
    /// <summary>
    /// `body` is a structured <see cref="Value"/> (despite the name -- no
    /// msgpack byte-level encoding actually happens; msgpack was removed
    /// from macula's own dependencies in v3.0.0, `body` is embedded as an
    /// ordinary nested value in the frame's own canonical-CBOR envelope
    /// either way).
    /// </summary>
    Msgpack,
}

public static class StreamEncodingNames
{
    public static string Name(this StreamEncoding encoding) => encoding switch
    {
        StreamEncoding.Raw => "raw",
        StreamEncoding.Msgpack => "msgpack",
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    public static StreamEncoding? FromName(string name) => name switch
    {
        "raw" => StreamEncoding.Raw,
        "msgpack" => StreamEncoding.Msgpack,
        _ => null,
    };
}

/// <summary>`role` on a STREAM_END -- which direction(s) are closing.</summary>
public enum StreamRole
{
    /// <summary>Half-close: this side is done sending, still willing to receive.</summary>
    Send,
    /// <summary>Full close: this side is done in both directions.</summary>
    Both,
}

public static class StreamRoleNames
{
    public static string Name(this StreamRole role) => role switch
    {
        StreamRole.Send => "send",
        StreamRole.Both => "both",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static StreamRole? FromName(string name) => name switch
    {
        "send" => StreamRole.Send,
        "both" => StreamRole.Both,
        _ => null,
    };
}

/// <summary>Fields for a STREAM_OPEN frame. Mirrors CALL's auth/routing shape plus the stream-specific fields.</summary>
public sealed class StreamOpenSpec
{
    public required byte[] StreamId { get; init; }
    public required string Procedure { get; init; }
    public required byte[] Realm { get; init; }
    public required StreamMode Mode { get; init; }
    public required Value Args { get; init; }
    public required long DeadlineMs { get; init; }
    public required byte[] Caller { get; init; }
    public byte[] SourceRoute { get; init; } = Array.Empty<byte>();
    public ulong RetryBudget { get; init; }
}

public static class StreamOpenFrame
{
    public static Value.MapValue Build(StreamOpenSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("stream_open", 0, frameId, sentAtMs)
            .WithField("stream_id", Value.Bytes(spec.StreamId))
            .WithField("procedure", Value.Bytes(Encoding.UTF8.GetBytes(spec.Procedure)))
            .WithField("realm", Value.Bytes(spec.Realm))
            .WithField("mode", Value.Text(spec.Mode.Name()))
            .WithField("args", spec.Args)
            .WithField("deadline_ms", Value.Int(spec.DeadlineMs))
            .WithField("caller", Value.Bytes(spec.Caller))
            .WithField("source_route", Value.Bytes(spec.SourceRoute))
            .WithField("retry_budget", Value.UInt(spec.RetryBudget));

    public static Value.MapValue Build(StreamOpenSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

/// <summary>The fields a provider needs from an inbound STREAM_OPEN -- the first frame on a freshly-accepted dedicated stream.</summary>
public sealed record StreamOpenInfo(byte[] StreamId, string Procedure, byte[] Realm, StreamMode Mode, Value Args, long DeadlineMs, byte[] Caller);

public static class StreamOpenFrameParsing
{
    public static StreamOpenInfo Parse(Value frame)
    {
        if (frame is not Value.MapValue map || map.Get("frame_type") is not Value.TextValue { } ft || ft.AsText() != "stream_open")
        {
            throw new ParseFrameException(ParseFrameError.WrongFrameType);
        }

        byte[] streamId = map.Get("stream_id") switch
        {
            Value.BytesValue { Value.Length: 16 } b => b.Value,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "stream_id"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "stream_id"),
        };
        string procedure = map.Get("procedure") switch
        {
            Value.BytesValue b => Encoding.UTF8.GetString(b.Value),
            null => throw new ParseFrameException(ParseFrameError.MissingField, "procedure"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "procedure"),
        };
        byte[] realm = map.Get("realm") switch
        {
            Value.BytesValue { Value.Length: 32 } b => b.Value,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "realm"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "realm"),
        };
        StreamMode mode = map.Get("mode") switch
        {
            Value.TextValue t => StreamModeNames.FromName(t.AsText()) ?? throw new ParseFrameException(ParseFrameError.WrongFieldType, "mode"),
            null => throw new ParseFrameException(ParseFrameError.MissingField, "mode"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "mode"),
        };
        var args = map.Get("args") ?? throw new ParseFrameException(ParseFrameError.MissingField, "args");
        long deadlineMs = map.Get("deadline_ms") switch
        {
            Value.UIntValue u => (long)u.Value,
            Value.NegIntValue n => -1L - (long)n.NMinusOne,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "deadline_ms"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "deadline_ms"),
        };
        byte[] caller = map.Get("caller") switch
        {
            Value.BytesValue { Value.Length: 32 } b => b.Value,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "caller"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "caller"),
        };

        return new StreamOpenInfo(streamId, procedure, realm, mode, args, deadlineMs, caller);
    }
}

/// <summary>Fields for a STREAM_DATA frame -- one chunk.</summary>
public sealed class StreamDataSpec
{
    public required byte[] StreamId { get; init; }
    public required ulong Seq { get; init; }
    public required StreamEncoding Encoding { get; init; }
    public required Value Body { get; init; }
}

public static class StreamDataFrame
{
    public static Value.MapValue Build(StreamDataSpec spec, byte[] frameId, ulong sentAtMs) =>
        // Like RESULT, STREAM_DATA does not touch the base envelope's
        // realm/call_id/source_route -- they stay Null.
        Envelope.Base("stream_data", 0, frameId, sentAtMs)
            .WithField("stream_id", Value.Bytes(spec.StreamId))
            .WithField("seq", Value.UInt(spec.Seq))
            .WithField("encoding", Value.Text(spec.Encoding.Name()))
            .WithField("body", spec.Body);

    public static Value.MapValue Build(StreamDataSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

public sealed class StreamEndSpec
{
    public required byte[] StreamId { get; init; }
    public required StreamRole Role { get; init; }
}

public static class StreamEndFrame
{
    public static Value.MapValue Build(StreamEndSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("stream_end", 0, frameId, sentAtMs)
            .WithField("stream_id", Value.Bytes(spec.StreamId))
            .WithField("role", Value.Text(spec.Role.Name()));

    public static Value.MapValue Build(StreamEndSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

/// <summary>
/// Fields for a STREAM_ERROR frame -- the explicit abort a well-behaved
/// peer sends instead of just dropping the stream on any non-normal
/// termination. `code` is a free-form label, NOT a BOLT#4 numeric code
/// like an ERROR frame's `code` -- streaming aborts and unary-call errors
/// use unrelated error vocabularies.
/// </summary>
public sealed class StreamErrorSpec
{
    public required byte[] StreamId { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public static class StreamErrorFrame
{
    public static Value.MapValue Build(StreamErrorSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("stream_error", 0, frameId, sentAtMs)
            .WithField("stream_id", Value.Bytes(spec.StreamId))
            .WithField("code", Value.Bytes(Encoding.UTF8.GetBytes(spec.Code)))
            .WithField("message", Value.Bytes(Encoding.UTF8.GetBytes(spec.Message)));

    public static Value.MapValue Build(StreamErrorSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

/// <summary>Fields for a STREAM_REPLY frame -- the terminal result of a client_stream/bidi exchange.</summary>
public sealed class StreamReplySpec
{
    public required byte[] StreamId { get; init; }
    public required Value Payload { get; init; }
    public required byte[] RespondedBy { get; init; }
}

public static class StreamReplyFrame
{
    public static Value.MapValue Build(StreamReplySpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("stream_reply", 0, frameId, sentAtMs)
            .WithField("stream_id", Value.Bytes(spec.StreamId))
            .WithField("payload", spec.Payload)
            .WithField("responded_by", Value.Bytes(spec.RespondedBy));

    public static Value.MapValue Build(StreamReplySpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

/// <summary>What a stream consumer actually receives -- one parsed STREAM_DATA/STREAM_END/STREAM_ERROR/STREAM_REPLY frame.</summary>
public abstract record StreamEvent
{
    private StreamEvent() { }

    public sealed record Data(byte[] StreamId, ulong Seq, StreamEncoding Encoding, Value Body) : StreamEvent;

    public sealed record End(byte[] StreamId, StreamRole Role) : StreamEvent;

    public sealed record Error(byte[] StreamId, string Code, string Message) : StreamEvent;

    public sealed record Reply(byte[] StreamId, Value Payload, byte[] RespondedBy) : StreamEvent;
}

public static class StreamFrameParsing
{
    /// <summary>Extract this frame's stream_id, regardless of frame type.</summary>
    public static byte[]? FrameStreamId(Value frame) =>
        frame is Value.MapValue map && map.Get("stream_id") is Value.BytesValue { Value.Length: 16 } b ? b.Value : null;

    public static StreamEvent ParseStreamEvent(Value frame)
    {
        if (frame is not Value.MapValue map)
        {
            throw new ParseFrameException(ParseFrameError.WrongFrameType);
        }

        byte[] streamId = map.Get("stream_id") switch
        {
            Value.BytesValue { Value.Length: 16 } b => b.Value,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "stream_id"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "stream_id"),
        };

        string frameType = map.Get("frame_type") is Value.TextValue t ? t.AsText() : "";
        switch (frameType)
        {
            case "stream_data":
            {
                ulong seq = map.Get("seq") switch
                {
                    Value.UIntValue u => u.Value,
                    null => throw new ParseFrameException(ParseFrameError.MissingField, "seq"),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "seq"),
                };
                StreamEncoding encoding = map.Get("encoding") switch
                {
                    Value.TextValue et => StreamEncodingNames.FromName(et.AsText())
                        ?? throw new ParseFrameException(ParseFrameError.WrongFieldType, "encoding"),
                    null => throw new ParseFrameException(ParseFrameError.MissingField, "encoding"),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "encoding"),
                };
                var body = map.Get("body") ?? throw new ParseFrameException(ParseFrameError.MissingField, "body");
                return new StreamEvent.Data(streamId, seq, encoding, body);
            }
            case "stream_end":
            {
                StreamRole role = map.Get("role") switch
                {
                    Value.TextValue rt => StreamRoleNames.FromName(rt.AsText())
                        ?? throw new ParseFrameException(ParseFrameError.WrongFieldType, "role"),
                    null => throw new ParseFrameException(ParseFrameError.MissingField, "role"),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "role"),
                };
                return new StreamEvent.End(streamId, role);
            }
            case "stream_error":
            {
                string code = map.Get("code") switch
                {
                    Value.BytesValue b => Encoding.UTF8.GetString(b.Value),
                    null => throw new ParseFrameException(ParseFrameError.MissingField, "code"),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "code"),
                };
                string message = map.Get("message") switch
                {
                    Value.BytesValue b => Encoding.UTF8.GetString(b.Value),
                    null => throw new ParseFrameException(ParseFrameError.MissingField, "message"),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "message"),
                };
                return new StreamEvent.Error(streamId, code, message);
            }
            case "stream_reply":
            {
                var payload = map.Get("payload") ?? throw new ParseFrameException(ParseFrameError.MissingField, "payload");
                byte[] respondedBy = map.Get("responded_by") switch
                {
                    Value.BytesValue { Value.Length: 32 } b => b.Value,
                    null => throw new ParseFrameException(ParseFrameError.MissingField, "responded_by"),
                    _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "responded_by"),
                };
                return new StreamEvent.Reply(streamId, payload, respondedBy);
            }
            default:
                throw new ParseFrameException(ParseFrameError.WrongFrameType);
        }
    }
}
