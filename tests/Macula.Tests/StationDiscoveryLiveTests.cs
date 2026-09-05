using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Dials the real production demo fleet -- same fleet-flakiness caveat
/// as <see cref="StationPoolLiveTests"/>, excluded from the default CI
/// run via the "Live" trait.
///
/// Proves StationDiscovery actually resolves hecate_stations.list_stations'
/// realm and adds real stations to a live pool end to end, against the
/// real fleet -- not just that the parsing/dedupe logic is right in
/// isolation (StationDiscoveryTests covers that). Mirrors the identical
/// live test in macula-go's own port of this feature (pool_discovery_live_test.go),
/// including the SAME real bug that test caught: a first cut preferring
/// host_advertised (a bare IPv6 literal on every real station) over
/// hostname failed every single discovered link's WebPki handshake --
/// fixed by preferring hostname, see DialTargetFromStationRow's own doc.
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class StationDiscoveryLiveTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Station_discovery_finds_and_connects_to_real_fleet_stations()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();

        await using var pool = StationPool.Connect(
            new[] { new Seed(StationHost, StationPort) },
            Connection.Trust.UseWebPki,
            identity,
            new StationPoolOptions
            {
                StationDiscovery = new StationDiscoveryOptions
                {
                    Enabled = true,
                    RefreshInterval = TimeSpan.FromHours(1), // one attempt is enough for this test
                    MaxLinks = 5,
                },
            });

        await WaitUntilAsync(() => pool.Status.HealthyLinks >= 1, TimeSpan.FromSeconds(15),
            "pool never completed its initial bootstrap handshake");

        // Give the background discovery task time to run its first
        // attempt (DHT lookup + list_stations call, both real network
        // round trips) -- this only proves discovery ITSELF worked
        // (a link was added for real stations), not that any of them
        // are healthy yet.
        await WaitUntilAsync(() => pool.Links.Count > 1, TimeSpan.FromSeconds(15),
            $"station discovery found no additional stations against the real fleet (linkCount={pool.Links.Count}) -- " +
            $"either hecate_stations.list_stations isn't currently advertised/visible from {StationHost}, or discovery has a real bug");

        var linkCount = pool.Links.Count;

        // SEPARATE budget for the newly discovered links' own dial+
        // handshake against real remote hosts -- distinct from the
        // discovery round trip above, so a slow/partially-unreachable
        // station doesn't get blamed on discovery logic itself.
        await WaitUntilAsync(() => pool.Status.HealthyLinks >= 2, TimeSpan.FromSeconds(20),
            $"discovered link(s) exist (linkCount={linkCount}) but didn't come up healthy within the wait window (healthy={pool.Status.HealthyLinks})");

        Assert.True(pool.Status.HealthyLinks >= 2, $"linkCount={linkCount} healthyLinks={pool.Status.HealthyLinks}");
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
