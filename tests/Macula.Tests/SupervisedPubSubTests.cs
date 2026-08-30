using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Live proof that the supervised pubsub pair works end to end: a real
/// subscriber genuinely receives a real event via its callback (not manual
/// polling), a real publisher's onDone fires with a clean outcome, and the
/// auto-published pubsub.publish_completed_v1 fact is confirmed by an
/// INDEPENDENT fourth watcher session -- not the publisher's own
/// bookkeeping. Same fleet-flakiness caveat as <see cref="LiveStationTests"/>.
/// Separate identities per role throughout.
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SupervisedPubSubTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Run_subscriber_and_run_publisher_against_the_real_fleet()
    {
        var subscriberId = KeyPair.GenerateWithDefaultPuzzle();
        var publisherId = KeyPair.GenerateWithDefaultPuzzle();
        var watcherId = KeyPair.GenerateWithDefaultPuzzle();
        var topic = $"macula_dotnet_sdk.pubsub_wrapper_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var subscriberSession = await Session.ConnectAsync(StationHost, StationPort, subscriberId, Connection.Trust.UseWebPki);
        await using var publisherSession = await Session.ConnectAsync(StationHost, StationPort, publisherId, Connection.Trust.UseWebPki);
        await using var watcherSession = await Session.ConnectAsync(StationHost, StationPort, watcherId, Connection.Trust.UseWebPki);

        var receivedEvent = new TaskCompletionSource<EventInfo>();
        using var subscriberCts = new CancellationTokenSource();
        var subscriberTask = SupervisedPubSub.RunSubscriberAsync(
            subscriberSession,
            new SubscribeSpec { Topic = topic, Realm = realm, Subscriber = subscriberId.NodeId() },
            subscriberId,
            evt =>
            {
                receivedEvent.TrySetResult(evt);
                return Task.CompletedTask;
            },
            subscriberCts.Token);

        // Independent watcher, subscribed to the completed-fact's fixed
        // global topic BEFORE the publish happens.
        var watcherReceived = new TaskCompletionSource<EventInfo>();
        using var watcherCts = new CancellationTokenSource();
        var watcherTask = SupervisedPubSub.RunSubscriberAsync(
            watcherSession,
            new SubscribeSpec { Topic = "pubsub.publish_completed_v1", Realm = realm, Subscriber = watcherId.NodeId() },
            watcherId,
            evt =>
            {
                watcherReceived.TrySetResult(evt);
                return Task.CompletedTask;
            },
            watcherCts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // let both subscriptions land

        var publishOutcome = new TaskCompletionSource<PublishOutcome>();
        SupervisedPubSub.RunPublisherAsync(
            publisherSession,
            new PublishSpec { Topic = topic, Realm = realm, Publisher = publisherId.NodeId(), Seq = 1, Payload = Value.Text("hello from the supervised publisher"), PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            publisherId,
            announce: true,
            onDone: outcome => publishOutcome.TrySetResult(outcome));

        var gotEvent = await WaitOrTimeout(receivedEvent.Task, TimeSpan.FromSeconds(10), "subscriber never received the event");
        Assert.Equal("hello from the supervised publisher", gotEvent.Payload.AsText());

        var outcome = await WaitOrTimeout(publishOutcome.Task, TimeSpan.FromSeconds(10), "publisher's onDone never fired");
        Assert.Null(outcome.Error);
        Assert.False(outcome.Cancelled);

        var watcherEvent = await WaitOrTimeout(watcherReceived.Task, TimeSpan.FromSeconds(10), "independent watcher never saw the auto-published completed fact");
        Assert.Equal("pubsub.publish_completed_v1", watcherEvent.Topic);

        subscriberCts.Cancel();
        watcherCts.Cancel();
        await AssertCancelsCleanly(subscriberTask);
        await AssertCancelsCleanly(watcherTask);
    }

    private static async Task<T> WaitOrTimeout<T>(Task<T> task, TimeSpan timeout, string message)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException(message);
        }
        return await task;
    }

    private static async Task AssertCancelsCleanly(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}
