using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Content;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Dials the real production demo fleet (station-de-frankfurt.macula.io) --
/// no uptime guarantee, must never block an unrelated PR. Excluded from
/// the default CI run via the "Live" trait; run explicitly with
/// `dotnet test --filter Category=Live`.
///
/// The bare `macula.io` hostname has an A record but no AAAA, while the
/// station's actual QUIC listener has no relationship to that A record --
/// dialing it directly resolves to a real, reachable IPv4 address with
/// nothing listening. `station-de-frankfurt.macula.io` is the name that
/// actually resolves to the listener (matches the same gotcha the Go and
/// Rust SDKs' own live tests already document).
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class LiveStationTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Connect_completes_a_real_handshake_against_the_live_fleet()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();

        await using var session = await Session.ConnectAsync(
            StationHost,
            StationPort,
            identity,
            Connection.Trust.UseWebPki,
            handshakeTimeout: TimeSpan.FromSeconds(15));

        Assert.True(session.RemoteInfo.Accepted);
        Assert.Equal(32, session.RemoteInfo.NodeId.Length);
    }

    [Fact]
    public async Task Unary_call_round_trip_against_a_nonexistent_procedure_reports_unknown_next_peer()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(StationHost, StationPort, identity, Connection.Trust.UseWebPki);

        var realm = new byte[32];
        var response = await session.CallAsync(
            "macula_csharp_sdk.definitely_not_a_real_procedure",
            realm,
            Value.Text("hello"),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000,
            TimeSpan.FromSeconds(10));

        var error = Assert.IsType<CallResponse.Error>(response);
        Assert.Equal("unknown_next_peer", error.Name);
    }

    [Fact]
    public async Task Publish_subscribe_round_trip_delivers_our_own_publish_directly()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(StationHost, StationPort, identity, Connection.Trust.UseWebPki);

        var realm = new byte[32];
        var topic = $"macula_csharp_sdk.test.{Guid.NewGuid():N}";

        await session.SubscribeAsync(new SubscribeSpec { Topic = topic, Realm = realm, Subscriber = identity.NodeId() });

        await session.PublishAsync(new PublishSpec
        {
            Topic = topic,
            Realm = realm,
            Publisher = identity.NodeId(),
            Seq = 1,
            Payload = Value.Text("hello mesh"),
            PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        var evt = await session.RecvEventAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(topic, evt.Topic);
        Assert.Equal("hello mesh", evt.Payload.AsText());
        Assert.Equal("direct", evt.DeliveredVia);
    }

    [Fact]
    public async Task Content_put_get_round_trips_a_single_block()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(StationHost, StationPort, identity, Connection.Trust.UseWebPki);

        var data = "hello from macula-csharp-sdk, single block"u8.ToArray();
        var mcid = await ContentTransfer.PutAsync(session, data, "greeting.txt", identity);
        Assert.False(Mcid.IsChunked(mcid));

        var fetched = await ContentTransfer.GetAsync(session, mcid, identity);
        Assert.Equal(data, fetched);
    }

    [Fact]
    public async Task Content_put_get_round_trips_chunked_content()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(StationHost, StationPort, identity, Connection.Trust.UseWebPki);

        // 3 chunks at the default 256 KiB chunk size.
        var data = new byte[ManifestBuilder.DefaultChunkSize * 2 + 12345];
        Random.Shared.NextBytes(data);

        var mcid = await ContentTransfer.PutAsync(session, data, "big-file.bin", identity);
        Assert.True(Mcid.IsChunked(mcid));

        var fetched = await ContentTransfer.GetAsync(session, mcid, identity);
        Assert.Equal(data, fetched);
    }

    [Fact]
    public async Task Content_get_reports_not_found_for_a_made_up_mcid()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(StationHost, StationPort, identity, Connection.Trust.UseWebPki);

        var madeUp = ManifestBuilder.BlockMcid("this content was never actually stored"u8.ToArray());
        var ex = await Assert.ThrowsAsync<ContentTransfer.ContentTransferException>(
            () => ContentTransfer.GetAsync(session, madeUp, identity));
        Assert.Equal(ContentTransfer.RemoteReason.NotFound, ex.Reason);
    }
}
