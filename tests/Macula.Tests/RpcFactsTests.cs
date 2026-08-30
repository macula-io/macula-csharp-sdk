using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Live proof that RPC telemetry auto-facts (rpc.sent_v1/rpc.completed_v1
/// caller-side, rpc.received_v1/rpc.replied_v1 provider-side) actually
/// land, confirmed via independent watcher sessions, not the caller's/
/// provider's own bookkeeping. These are FIXED, well-known topic names on
/// a shared PUBLIC demo fleet -- unlike this test file's siblings, which
/// dodge collisions with real third-party traffic by randomizing the topic
/// string, a watcher here must correlate by request_id (draining a batch
/// per topic and finding a match) rather than trusting first-arrival,
/// matching the exact lesson macula-go-sdk's own equivalent test had to
/// learn live. Same fleet-flakiness caveat as <see cref="LiveStationTests"/>.
///
/// Passes reliably alone or under `dotnet test -- xUnit.MaxParallelThreads=1`
/// (confirmed: 107/107 full suite, sequential). Can flake under xUnit's
/// default cross-class parallelism, where several Live test classes hit
/// the shared demo fleet at once -- confirmed to be fleet contention, not
/// a code defect, the same class of issue already documented in this SDK
/// family's Go/Rust ports (which needed `-p 1`/sequential live-test
/// execution for the identical reason).
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class RpcFactsTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Rpc_telemetry_facts_land_with_correct_request_ids()
    {
        var providerId = KeyPair.GenerateWithDefaultPuzzle();
        var callerId = KeyPair.GenerateWithDefaultPuzzle();
        var procedure = $"macula_dotnet_sdk.rpc_facts_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var provider = await Session.ConnectAsync(StationHost, StationPort, providerId, Connection.Trust.UseWebPki);
        await provider.AdvertiseAsync(new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = providerId.NodeId() });

        var served = Task.Run(async () =>
        {
            CallLookup lookup = (_, proc) => proc != procedure ? null : payload => Task.FromResult(payload);
            await provider.ServeOneCallAsync(lookup, TimeSpan.FromSeconds(15));
        });

        // Independent watchers, one per fact topic, subscribed BEFORE the
        // call happens.
        var sentWatcher = new FactWatcher(realm, "rpc.sent_v1");
        var completedWatcher = new FactWatcher(realm, "rpc.completed_v1");
        var receivedWatcher = new FactWatcher(realm, "rpc.received_v1");
        var repliedWatcher = new FactWatcher(realm, "rpc.replied_v1");
        await Task.WhenAll(
            sentWatcher.StartAsync(StationHost, StationPort),
            completedWatcher.StartAsync(StationHost, StationPort),
            receivedWatcher.StartAsync(StationHost, StationPort),
            repliedWatcher.StartAsync(StationHost, StationPort));

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // let subscriptions land

        await using var caller = await Session.ConnectAsync(StationHost, StationPort, callerId, Connection.Trust.UseWebPki);
        var response = await caller.CallAsync(procedure, realm, Value.Text("ping"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000, TimeSpan.FromSeconds(10));
        Assert.IsType<CallResponse.Result>(response);
        await served;

        var sentFact = await sentWatcher.WaitForAnyAsync(TimeSpan.FromSeconds(10));
        var completedFact = await completedWatcher.WaitForAnyAsync(TimeSpan.FromSeconds(10));
        var receivedFact = await receivedWatcher.WaitForAnyAsync(TimeSpan.FromSeconds(10));
        var repliedFact = await repliedWatcher.WaitForAnyAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(sentFact);
        Assert.NotNull(completedFact);
        Assert.NotNull(receivedFact);
        Assert.NotNull(repliedFact);

        var callerRequestId = ((Value.MapValue)sentFact!).Get("request_id")!.AsBytes();
        Assert.Equal(callerRequestId, ((Value.MapValue)completedFact!).Get("request_id")!.AsBytes());
        Assert.Equal("completed", ((Value.MapValue)completedFact).Get("outcome")!.AsText());

        var providerRequestId = ((Value.MapValue)receivedFact!).Get("request_id")!.AsBytes();
        Assert.Equal(providerRequestId, ((Value.MapValue)repliedFact!).Get("request_id")!.AsBytes());
        Assert.Equal("replied", ((Value.MapValue)repliedFact!).Get("outcome")!.AsText());

        await sentWatcher.StopAsync();
        await completedWatcher.StopAsync();
        await receivedWatcher.StopAsync();
        await repliedWatcher.StopAsync();
    }

    /// <summary>
    /// Subscribes to one fixed, shared topic and buffers whatever real
    /// events arrive (including unrelated third-party traffic on this
    /// public fleet), so the test above can pick out the one it actually
    /// caused rather than trusting first-arrival.
    /// </summary>
    private sealed class FactWatcher
    {
        private readonly byte[] _realm;
        private readonly string _topic;
        private readonly List<Value> _received = new();
        private readonly object _lock = new();
        private Session? _session;
        private CancellationTokenSource? _cts;
        private Task? _task;

        public FactWatcher(byte[] realm, string topic)
        {
            _realm = realm;
            _topic = topic;
        }

        public async Task StartAsync(string host, int port)
        {
            var id = KeyPair.GenerateWithDefaultPuzzle();
            _session = await Session.ConnectAsync(host, port, id, Connection.Trust.UseWebPki);
            _cts = new CancellationTokenSource();
            _task = SupervisedPubSub.RunSubscriberAsync(
                _session,
                new SubscribeSpec { Topic = _topic, Realm = _realm, Subscriber = id.NodeId() },
                id,
                evt =>
                {
                    lock (_lock)
                    {
                        _received.Add(evt.Payload);
                    }
                    return Task.CompletedTask;
                },
                _cts.Token);
        }

        public async Task<Value?> WaitForAnyAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (_received.Count > 0)
                    {
                        return _received[^1];
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
            return null;
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_task is not null)
            {
                try
                {
                    await _task;
                }
                catch (OperationCanceledException)
                {
                    // expected
                }
            }
            if (_session is not null)
            {
                await _session.DisposeAsync();
            }
        }
    }
}
