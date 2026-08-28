using System.Text;

namespace Macula.Frame;

public sealed class PublishSpec
{
    public required string Topic { get; init; }
    public required byte[] Realm { get; init; }
    public required byte[] Publisher { get; init; }
    public required ulong Seq { get; init; }
    public required Value Payload { get; init; }
    public required ulong PublishedAtMs { get; init; }
    public ulong? TtlMs { get; init; }
}

public static class PublishFrame
{
    public static Value.MapValue Build(PublishSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("publish", 0, frameId, sentAtMs)
            .WithField("realm", Value.Bytes(spec.Realm))
            // `topic := binary()` -- bytes, not text.
            .WithField("topic", Value.Bytes(Encoding.UTF8.GetBytes(spec.Topic)))
            .WithField("publisher", Value.Bytes(spec.Publisher))
            .WithField("seq", Value.UInt(spec.Seq))
            .WithField("payload", spec.Payload)
            .WithField("published_at_ms", Value.UInt(spec.PublishedAtMs))
            .WithField("ttl_ms", spec.TtlMs is { } ttl ? Value.UInt(ttl) : Value.Null);

    /// <summary>
    /// Build a PUBLISH frame with a fresh frame_id/sent_at_ms. Does not set
    /// `publisher_sig` (the separate end-to-end publisher signature) -- not
    /// implemented by this SDK yet, matching the sibling Go/Rust SDKs.
    /// </summary>
    public static Value.MapValue Build(PublishSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

public sealed class SubscribeSpec
{
    public required string Topic { get; init; }
    public required byte[] Realm { get; init; }
    public required byte[] Subscriber { get; init; }
}

public static class SubscribeFrame
{
    public static Value.MapValue Build(SubscribeSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("subscribe", 0, frameId, sentAtMs)
            .WithField("realm", Value.Bytes(spec.Realm))
            .WithField("topic", Value.Bytes(Encoding.UTF8.GetBytes(spec.Topic)))
            .WithField("subscriber", Value.Bytes(spec.Subscriber))
            .WithField("filter", Value.Null)
            .WithField("options", Value.Map(Array.Empty<KeyValuePair<Value, Value>>()));

    /// <summary>Build a SUBSCRIBE frame. No filter, no options -- the plainest possible subscription.</summary>
    public static Value.MapValue Build(SubscribeSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

public sealed class UnsubscribeSpec
{
    public required string Topic { get; init; }
    public required byte[] Realm { get; init; }
    public required byte[] Subscriber { get; init; }
}

public static class UnsubscribeFrame
{
    public static Value.MapValue Build(UnsubscribeSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("unsubscribe", 0, frameId, sentAtMs)
            .WithField("realm", Value.Bytes(spec.Realm))
            .WithField("topic", Value.Bytes(Encoding.UTF8.GetBytes(spec.Topic)))
            .WithField("subscriber", Value.Bytes(spec.Subscriber));

    public static Value.MapValue Build(UnsubscribeSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

/// <summary>What a subscriber actually receives -- parsed fields of an EVENT frame.</summary>
public sealed record EventInfo(string Topic, byte[] Realm, byte[] Publisher, ulong Seq, Value Payload, string DeliveredVia);

public static class EventFrameParsing
{
    public static EventInfo Parse(Value frame)
    {
        if (frame is not Value.MapValue map || map.Get("frame_type") is not Value.TextValue { } ft || ft.AsText() != "event")
        {
            throw new ParseFrameException(ParseFrameError.WrongFrameType);
        }

        // `topic := binary()` on the wire -- bytes, not text.
        string topic = map.Get("topic") switch
        {
            Value.BytesValue b => Encoding.UTF8.GetString(b.Value),
            null => throw new ParseFrameException(ParseFrameError.MissingField, "topic"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "topic"),
        };
        byte[] realm = map.Get("realm") switch
        {
            Value.BytesValue { Value.Length: 32 } b => b.Value,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "realm"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "realm"),
        };
        byte[] publisher = map.Get("publisher") switch
        {
            Value.BytesValue { Value.Length: 32 } b => b.Value,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "publisher"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "publisher"),
        };
        ulong seq = map.Get("seq") switch
        {
            Value.UIntValue u => u.Value,
            null => throw new ParseFrameException(ParseFrameError.MissingField, "seq"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "seq"),
        };
        var payload = map.Get("payload") ?? throw new ParseFrameException(ParseFrameError.MissingField, "payload");
        string deliveredVia = map.Get("delivered_via") switch
        {
            Value.TextValue t => t.AsText(),
            null => throw new ParseFrameException(ParseFrameError.MissingField, "delivered_via"),
            _ => throw new ParseFrameException(ParseFrameError.WrongFieldType, "delivered_via"),
        };

        return new EventInfo(topic, realm, publisher, seq, payload, deliveredVia);
    }
}
