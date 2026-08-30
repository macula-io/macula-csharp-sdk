using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Resolves the "UNVERIFIED RISK" flagged in <see cref="Session.CloseAsync"/>'s
/// own doc comment since 2026-08-29: the Go and Rust ports of connect-write-
/// immediately-close both had a real, confirmed data-loss bug (a PUBLISH
/// sent immediately before a hard connection-close could be silently
/// dropped, since the underlying QUIC library's write only queues data for
/// a background sender and returns before it's actually on the wire). That
/// risk was previously unactionable here because live testing was believed
/// impossible on this machine -- no longer true (see
/// project_macula_dotnet_sdk memory / reference_dotnet_quic_gotchas for the
/// libmsquic workaround this test relies on).
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class CloseDataLossTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Publish_immediately_followed_by_close_survives_reliably()
    {
        var watcherId = KeyPair.GenerateWithDefaultPuzzle();
        var topic = $"macula_dotnet_sdk.close_data_loss_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var watcherSession = await Session.ConnectAsync(StationHost, StationPort, watcherId, Connection.Trust.UseWebPki);
        var received = new List<string>();
        var receivedLock = new object();
        using var watcherCts = new CancellationTokenSource();
        var watcherTask = SupervisedPubSub.RunSubscriberAsync(
            watcherSession,
            new SubscribeSpec { Topic = topic, Realm = realm, Subscriber = watcherId.NodeId() },
            watcherId,
            evt =>
            {
                lock (receivedLock)
                {
                    received.Add(evt.Payload.AsText());
                }
                return Task.CompletedTask;
            },
            watcherCts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // let the subscription land

        const int attempts = 10;
        for (var i = 0; i < attempts; i++)
        {
            var publisherId = KeyPair.GenerateWithDefaultPuzzle();
            var session = await Session.ConnectAsync(StationHost, StationPort, publisherId, Connection.Trust.UseWebPki);
            await session.PublishAsync(new PublishSpec
            {
                Topic = topic,
                Realm = realm,
                Publisher = publisherId.NodeId(),
                Seq = (ulong)i,
                Payload = Value.Text($"message-{i}"),
                PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            // The exact shape of the risk: close IMMEDIATELY after publish,
            // no delay, no drain wait of the caller's own.
            await session.CloseAsync();
        }

        // Give the mesh a real window to deliver everything before checking --
        // this is about whether the WRITE survived the close, not about
        // network latency.
        await Task.Delay(TimeSpan.FromSeconds(3));

        List<string> snapshot;
        lock (receivedLock)
        {
            snapshot = new List<string>(received);
        }

        watcherCts.Cancel();
        try
        {
            await watcherTask;
        }
        catch (Exception)
        {
            // Cancelling the watcher's subscription is cleanup, not part of
            // what this test verifies (whether the publishes survived,
            // captured in `snapshot` above already) -- a cancelled
            // System.Net.Quic read can escalate to a stream abort rather
            // than a clean OperationCanceledException, a separate,
            // secondary quirk not chased further here.
        }

        var expected = Enumerable.Range(0, attempts).Select(i => $"message-{i}").ToList();
        var missing = expected.Except(snapshot).ToList();
        Assert.True(missing.Count == 0,
            $"CloseAsync data-loss risk REPRODUCED: {missing.Count}/{attempts} publish(es) immediately followed by " +
            $"CloseAsync were lost (missing: {string.Join(", ", missing)}). This confirms the same bug Go/Rust had " +
            "and fixed -- CloseAsync needs to finish the stream then give the background sender a bounded window " +
            "before closing the connection, matching their fix.");
    }
}
