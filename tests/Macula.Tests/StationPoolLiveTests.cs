using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Dials the real production demo fleet. Same fleet-flakiness caveat as
/// <see cref="LiveStationTests"/> -- excluded from the default CI run via
/// the "Live" trait.
///
/// The core requirement this file exists to satisfy: prove
/// <see cref="StationPool"/> detects an ACTUAL connection drop against a
/// live station and reconnects with subscription replay -- not just that
/// isolated retry-scheduling logic fires in a unit test. Nothing on this
/// box can kill the production station's own process, so the drop is
/// forced a different, still-genuine way: a macula station kicks the
/// instant a SECOND connection arrives under the SAME identity
/// (macula_station_listener.erl's own per-identity dedupe -- confirmed
/// live by a sibling SDK session, github-com-94, the same day this pool
/// was built: "two Sessions under one identity kicking each other in a
/// loop until split apart"). Opening a second Session under the pool's
/// own identity forces the STATION itself to drop one of the two
/// connections -- a real, station-initiated disconnect, not a client-side
/// simulation.
///
/// This test does NOT assume which of the two connections the station
/// drops -- it explicitly waits for and asserts the pool's link actually
/// going unhealthy before checking reconnect, so a wrong assumption about
/// which side gets kicked fails loudly with a clear message instead of
/// silently passing without ever exercising a real respawn.
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class StationPoolLiveTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Pool_reconnects_and_replays_subscriptions_after_a_station_initiated_disconnect()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        var realm = new byte[32];
        var topic = $"macula_dotnet_sdk.pool_reconnect_test.{Guid.NewGuid():N}";

        await using var pool = StationPool.Connect(
            new[] { new Seed(StationHost, StationPort) },
            Connection.Trust.UseWebPki,
            identity);

        await WaitUntilAsync(() => pool.Status.HealthyLinks == 1, TimeSpan.FromSeconds(15),
            "pool never completed its initial handshake");

        var received = new ConcurrentQueue<string>();
        await pool.SubscribeAsync(realm, topic, evt =>
        {
            received.Enqueue(evt.Payload.AsText());
            return Task.CompletedTask;
        });

        await PublishOnceAsync(realm, topic, "before-kick");
        await WaitUntilAsync(() => received.Contains("before-kick"), TimeSpan.FromSeconds(10),
            "subscriber never saw the pre-kick publish -- the subscription never reached the wire, so this test cannot prove anything about reconnect");

        // Force a real, station-initiated disconnect of the pool's one
        // link. See this class's own doc for why this specific mechanism.
        await using (await Session.ConnectAsync(StationHost, StationPort, identity, Connection.Trust.UseWebPki))
        {
            await WaitUntilAsync(() => pool.Status.HealthyLinks == 0, TimeSpan.FromSeconds(10),
                "the pool's link was not dropped by the same-identity collision -- either the station kept the OLDER " +
                "connection instead of the newer one (opposite of this test's assumption), or the collision did not " +
                "trigger at all. This test's forcing mechanism needs revisiting, not the pool itself.");
        }

        await WaitUntilAsync(() => pool.Status.HealthyLinks == 1, TimeSpan.FromSeconds(20),
            "pool never reconnected after the station-initiated disconnect");

        await PublishOnceAsync(realm, topic, "after-reconnect");
        await WaitUntilAsync(() => received.Contains("after-reconnect"), TimeSpan.FromSeconds(10),
            "the subscription was not replayed onto the respawned link -- no delivery after reconnect");
    }

    private static async Task PublishOnceAsync(byte[] realm, string topic, string payload)
    {
        var publisherId = KeyPair.GenerateWithDefaultPuzzle();
        await using var publisher = await Session.ConnectAsync(StationHost, StationPort, publisherId, Connection.Trust.UseWebPki);
        await publisher.PublishAsync(new PublishSpec
        {
            Topic = topic,
            Realm = realm,
            Publisher = publisherId.NodeId(),
            Seq = 1,
            Payload = Value.Text(payload),
            PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        Assert.Fail(message);
    }
}
