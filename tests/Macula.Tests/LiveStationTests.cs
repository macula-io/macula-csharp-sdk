using System.Runtime.Versioning;
using Macula.Connection;
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
}
