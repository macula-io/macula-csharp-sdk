using System.Text;

namespace Macula.Frame;

/// <summary>
/// RPC advertise: ADVERTISE/UNADVERTISE. The provider-role building block --
/// registers this connection as the handler for `procedure` under `realm`;
/// the station then routes inbound CALLs (control stream) and STREAM_OPENs
/// (a fresh dedicated stream it opens toward us) for that procedure back to us.
/// </summary>
public sealed class AdvertiseSpec
{
    public required byte[] Realm { get; init; }
    public required string Procedure { get; init; }
    public required byte[] Advertiser { get; init; }
}

public static class AdvertiseFrame
{
    public static Value.MapValue Build(AdvertiseSpec spec, byte[] frameId, ulong sentAtMs) =>
        // `source_route` stays untouched (Null) -- unlike CALL/STREAM_OPEN,
        // which do override it. `realm` IS overridden here.
        Envelope.Base("advertise", 0, frameId, sentAtMs)
            .WithField("realm", Value.Bytes(spec.Realm))
            .WithField("procedure", Value.Bytes(Encoding.UTF8.GetBytes(spec.Procedure)))
            .WithField("advertiser", Value.Bytes(spec.Advertiser))
            // `options` has no known use case yet -- always the reference's own default, an empty map.
            .WithField("options", Value.Map(Array.Empty<KeyValuePair<Value, Value>>()));

    public static Value.MapValue Build(AdvertiseSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}

public sealed class UnadvertiseSpec
{
    public required byte[] Realm { get; init; }
    public required string Procedure { get; init; }
    public required byte[] Advertiser { get; init; }
}

public static class UnadvertiseFrame
{
    public static Value.MapValue Build(UnadvertiseSpec spec, byte[] frameId, ulong sentAtMs) =>
        Envelope.Base("unadvertise", 0, frameId, sentAtMs)
            .WithField("realm", Value.Bytes(spec.Realm))
            .WithField("procedure", Value.Bytes(Encoding.UTF8.GetBytes(spec.Procedure)))
            .WithField("advertiser", Value.Bytes(spec.Advertiser));

    public static Value.MapValue Build(UnadvertiseSpec spec) =>
        Build(spec, Envelope.FreshFrameId(), Envelope.CurrentMillis());
}
