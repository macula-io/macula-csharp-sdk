using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>
/// Subscribe to a topic, publish to it, and receive the resulting EVENT --
/// a subscriber does receive its own publish, delivered_via "direct".
///
/// <see cref="Session.RecvEventAsync"/> errors on the first non-EVENT
/// frame rather than silently skipping it, matching the sibling Go/Rust/PHP
/// SDKs: a caller waiting specifically for a pubsub delivery has no reason
/// to expect anything else on an otherwise-idle control stream. In
/// practice against the real station, that assumption needs a caveat --
/// it periodically sends unprompted `advertise` frames for its own
/// built-in `_content.*` procedures over every connected client's control
/// stream (observed directly, not documented anywhere), so a real caller
/// should loop past frames it doesn't care about rather than call
/// RecvEventAsync exactly once.
/// </summary>
public static class PublishSubscribe
{
    public static async Task RunAsync()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki);

        // A random realm, not the all-zero sentinel: this is a shared
        // public demo station, and realm=zero is exactly the sentinel
        // macula's own content-transfer procedures use, so it sees
        // meaningfully more unrelated traffic than a random realm does.
        var realm = new byte[32];
        Random.Shared.NextBytes(realm);
        var topic = $"macula_csharp_sdk.examples.{Guid.NewGuid():N}";

        await session.SubscribeAsync(new SubscribeSpec { Topic = topic, Realm = realm, Subscriber = identity.NodeId() });
        Console.WriteLine($"subscribed to {topic}");

        await session.PublishAsync(new PublishSpec
        {
            Topic = topic,
            Realm = realm,
            Publisher = identity.NodeId(),
            Seq = 1,
            Payload = Value.Text("hello mesh"),
            PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        Console.WriteLine("published");

        var evt = await RecvEventSkippingOtherTrafficAsync(session, TimeSpan.FromSeconds(10));
        Console.WriteLine($"received EVENT: topic={evt.Topic} payload={evt.Payload.AsText()} delivered_via={evt.DeliveredVia}");
    }

    /// <summary>
    /// Loops <see cref="Session.RecvAsync"/> until an EVENT arrives, discarding
    /// anything else -- see this class's own doc comment for why a real
    /// caller needs this instead of a single <see cref="Session.RecvEventAsync"/> call.
    /// </summary>
    private static async Task<EventInfo> RecvEventSkippingOtherTrafficAsync(Session session, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var frame = await session.RecvAsync(cts.Token);
            if (frame is Value.MapValue map && map.Get("frame_type") is Value.TextValue { } ft && ft.AsText() == "event")
            {
                return EventFrameParsing.Parse(frame);
            }
        }
    }
}
