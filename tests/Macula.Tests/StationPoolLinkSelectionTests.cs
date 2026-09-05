using System.Runtime.Versioning;
using Macula.Connection;

namespace Macula.Tests;

/// <summary>
/// Pure logic, no network -- StationPool's only constructor path
/// (Connect) immediately dials real Sessions with no fake-dialer test
/// seam (unlike macula-go's pool package), so these tests exercise the
/// extracted static cores (ResolveLinkSelection, SelectLinksCore)
/// directly rather than a live StationPool. Integration-level
/// behavior (does CallAsync/PublishAsync actually rotate across real
/// links) is covered by StationPoolLiveTests instead, matching this
/// repo's own existing convention of live-testing StationPool rather
/// than a from-scratch fake-session harness.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class StationPoolLinkSelectionTests
{
    [Fact]
    public void Auto_resolves_to_first_success_when_discovery_is_off()
    {
        Assert.Equal(LinkSelection.FirstSuccess, StationPool.ResolveLinkSelection(LinkSelection.Auto, stationDiscoveryEnabled: false));
    }

    [Fact]
    public void Auto_resolves_to_random_when_discovery_is_on()
    {
        Assert.Equal(LinkSelection.Random, StationPool.ResolveLinkSelection(LinkSelection.Auto, stationDiscoveryEnabled: true));
    }

    [Fact]
    public void Explicit_first_success_survives_even_with_discovery_enabled()
    {
        Assert.Equal(LinkSelection.FirstSuccess, StationPool.ResolveLinkSelection(LinkSelection.FirstSuccess, stationDiscoveryEnabled: true));
    }

    [Fact]
    public void Explicit_random_survives_even_with_discovery_disabled()
    {
        Assert.Equal(LinkSelection.Random, StationPool.ResolveLinkSelection(LinkSelection.Random, stationDiscoveryEnabled: false));
    }

    private static PooledLink Link(string host, long ordinal = 0) => new(new Seed(host, 4433), ordinal);

    [Fact]
    public void FirstSuccess_returns_every_link_unshuffled()
    {
        var links = new List<PooledLink> { Link("a.example"), Link("b.example"), Link("c.example") };
        var selected = StationPool.SelectLinksCore(links, LinkSelection.FirstSuccess);
        Assert.Same(links, selected); // passed through unchanged, not even a copy
    }

    [Fact]
    public void Random_with_zero_or_one_link_is_a_no_op()
    {
        var empty = new List<PooledLink>();
        Assert.Same(empty, StationPool.SelectLinksCore(empty, LinkSelection.Random));

        var one = new List<PooledLink> { Link("a.example") };
        Assert.Same(one, StationPool.SelectLinksCore(one, LinkSelection.Random));
    }

    [Fact]
    public void Random_returns_the_same_set_of_links_every_time()
    {
        var links = new List<PooledLink> { Link("a.example"), Link("b.example"), Link("c.example"), Link("d.example"), Link("e.example") };
        for (var i = 0; i < 20; i++)
        {
            var selected = StationPool.SelectLinksCore(links, LinkSelection.Random);
            Assert.Equal(links.Count, selected.Count);
            Assert.Equal(links.ToHashSet(), selected.ToHashSet());
        }
    }

    /// <summary>
    /// Proves the shuffle actually runs -- not relying on any
    /// incidental variation. Note this deliberately tests SelectLinksCore
    /// in isolation with an arbitrary input list order, NOT via
    /// ConnectedLinksSnapshot's own ordering: _links is a
    /// ConcurrentDictionary, whose enumeration order is hash-bucket
    /// order, not insertion order -- ConnectedLinksSnapshot restores a
    /// deterministic Seed-list order via PooledLink.Ordinal before
    /// SelectLinksCore ever sees the list (see that field's own doc for
    /// why this had to be explicit, found in adversarial review
    /// 2026-09-05). 5 links give 120 possible orderings; seeing fewer
    /// than 5 distinct ones across 200 calls would mean the shuffle
    /// isn't running.
    /// </summary>
    [Fact]
    public void Random_produces_multiple_distinct_orderings()
    {
        var links = new List<PooledLink> { Link("a.example"), Link("b.example"), Link("c.example"), Link("d.example"), Link("e.example") };
        var distinct = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var selected = StationPool.SelectLinksCore(links, LinkSelection.Random);
            distinct.Add(string.Join(",", selected.Select(l => l.Seed.Host)));
        }
        Assert.True(distinct.Count >= 5, $"expected at least 5 distinct orderings across 200 calls, got {distinct.Count}");
    }

    [Fact]
    public void Random_never_mutates_the_input_list()
    {
        var links = new List<PooledLink> { Link("a.example"), Link("b.example"), Link("c.example") };
        var snapshot = links.ToList();
        for (var i = 0; i < 20; i++)
        {
            StationPool.SelectLinksCore(links, LinkSelection.Random);
        }
        Assert.Equal(snapshot, links); // original list's own order is untouched
    }
}
