using System.Runtime.Versioning;
using Macula.Connection;

namespace Macula.Tests;

/// <summary>
/// Pure logic, no network -- see StationPoolLinkSelectionTests' own
/// header doc for why these test the extracted static cores directly
/// rather than a live StationPool.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class StationDiscoveryTests
{
    private static Value Row(params (string Key, Value Val)[] fields) =>
        Value.Map(fields.Select(f => new KeyValuePair<Value, Value>(Value.Text(f.Key), f.Val)).ToList());

    /// <summary>
    /// Regression test for a real bug caught live (2026-09-05, first
    /// found in the macula-go port of this same feature, cross-checked
    /// here): every station on the real fleet advertises
    /// host_advertised as a bare IPv6 literal, never a DNS name, so
    /// dialing it under WebPki trust always fails TLS cert validation.
    /// hostname is the field actually covered by each station's own
    /// certificate and must be preferred whenever both are present --
    /// matches a real mesh_list_stations row shape exactly.
    /// </summary>
    [Fact]
    public void DialTarget_prefers_hostname_over_bare_ip_in_host_advertised()
    {
        var row = Row(
            ("hostname", Value.Bytes("station-de-frankfurt.macula.io"u8.ToArray())),
            ("host_advertised", Value.List(new List<Value> { Value.Bytes("2a01:7e01::f03c:94ff:fe22:719e"u8.ToArray()) })),
            ("quic_port", Value.UInt(4433)));

        Assert.True(StationPool.DialTargetFromStationRow(row, out var host, out var port));
        Assert.Equal("station-de-frankfurt.macula.io", host);
        Assert.Equal(4433, port);
    }

    /// <summary>
    /// Covers the no-node_record-yet case (confirmed live: one real row
    /// had host_advertised + quic_port but no hostname/city/kind at
    /// all) -- there's still something dialable there, just not
    /// WebPki-safe unless the deployment's cert setup happens to cover
    /// it; not this function's job to judge that, only to prefer the
    /// better field when it exists.
    /// </summary>
    [Fact]
    public void DialTarget_falls_back_to_host_advertised_when_hostname_is_absent()
    {
        var row = Row(
            ("host_advertised", Value.List(new List<Value> { Value.Bytes("2600:3c0b::2000:1fff:fe35:416b"u8.ToArray()) })),
            ("quic_port", Value.UInt(4433)));

        Assert.True(StationPool.DialTargetFromStationRow(row, out var host, out var port));
        Assert.Equal("2600:3c0b::2000:1fff:fe35:416b", host);
        Assert.Equal(4433, port);
    }

    [Fact]
    public void DialTarget_rejects_a_row_with_neither_field()
    {
        var row = Row(("quic_port", Value.UInt(4433)));
        Assert.False(StationPool.DialTargetFromStationRow(row, out _, out _));
    }

    [Fact]
    public void DialTarget_rejects_missing_quic_port()
    {
        var row = Row(("hostname", Value.Bytes("station-de-frankfurt.macula.io"u8.ToArray())));
        Assert.False(StationPool.DialTargetFromStationRow(row, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void DialTarget_rejects_an_out_of_range_port(long badPort)
    {
        var row = Row(
            ("hostname", Value.Bytes("station-de-frankfurt.macula.io"u8.ToArray())),
            ("quic_port", Value.Int(badPort)));
        Assert.False(StationPool.DialTargetFromStationRow(row, out _, out _));
    }

    [Fact]
    public void DialTarget_rejects_an_empty_hostname_and_falls_back()
    {
        var row = Row(
            ("hostname", Value.Bytes(Array.Empty<byte>())),
            ("host_advertised", Value.List(new List<Value> { Value.Bytes("2600:3c0b::2000:1fff:fe35:416b"u8.ToArray()) })),
            ("quic_port", Value.UInt(4433)));

        Assert.True(StationPool.DialTargetFromStationRow(row, out var host, out _));
        Assert.Equal("2600:3c0b::2000:1fff:fe35:416b", host);
    }

    [Fact]
    public void StationNodeId_extracts_a_32_byte_node_id()
    {
        var nodeId = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        var row = Row(("node_id", Value.Bytes(nodeId)));
        Assert.True(StationPool.TryStationNodeId(row, out var extracted));
        Assert.Equal(nodeId, extracted);
    }

    [Fact]
    public void StationNodeId_rejects_the_wrong_length()
    {
        var row = Row(("node_id", Value.Bytes(new byte[] { 1, 2, 3 })));
        Assert.False(StationPool.TryStationNodeId(row, out _));
    }

    [Fact]
    public void StationNodeId_rejects_a_missing_field()
    {
        var row = Row(("hostname", Value.Bytes("station-de-frankfurt.macula.io"u8.ToArray())));
        Assert.False(StationPool.TryStationNodeId(row, out _));
    }

    private static PooledLink LinkWithNodeId(byte[]? nodeId) => new(new Seed("x.example", 4433), 0) { LastKnownNodeId = nodeId };

    /// <summary>
    /// Regression test for a real gap caught by adversarial review of
    /// the macula-go port of this same feature: a station already
    /// connected via a bootstrap Seed must be recognized by its PEER
    /// IDENTITY even if a later-discovered row names it under a
    /// differently-spelled host (case, a CNAME, an IP-literal seed) --
    /// _links' own key-based dedupe (by Seed) would not catch that.
    /// </summary>
    [Fact]
    public void HasLinkForNodeId_matches_regardless_of_host_spelling()
    {
        var nodeId = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var links = new List<PooledLink> { LinkWithNodeId(nodeId) };
        Assert.True(StationPool.HasLinkForNodeIdCore(links, nodeId));
    }

    [Fact]
    public void HasLinkForNodeId_is_false_for_an_unknown_node_id()
    {
        var known = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var unknown = Enumerable.Repeat((byte)0x66, 32).ToArray();
        var links = new List<PooledLink> { LinkWithNodeId(known) };
        Assert.False(StationPool.HasLinkForNodeIdCore(links, unknown));
    }

    [Fact]
    public void HasLinkForNodeId_is_false_when_no_link_has_handshaked_yet()
    {
        var nodeId = Enumerable.Repeat((byte)0x55, 32).ToArray();
        var links = new List<PooledLink> { LinkWithNodeId(null) };
        Assert.False(StationPool.HasLinkForNodeIdCore(links, nodeId));
    }
}
