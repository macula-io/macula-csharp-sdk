using Macula.Dht;
using Macula.Frame;

namespace Macula.Connection;

/// <summary>
/// StationPool's opt-in station-discovery half -- see
/// <see cref="StationDiscoveryOptions"/>'s own doc for the config
/// contract. Split into its own file (a second half of the partial
/// <see cref="StationPool"/> class) purely for readability; it shares
/// every private field the main file declares.
///
/// Both DHT calls this file makes route through THIS POOL'S OWN
/// CallAsync, never a bare Session pulled off a link directly: a
/// PooledLink's Session is read/written exclusively by its own
/// RunLinkAsync/PumpAsync pair (the class-level doc on "one physical
/// stream, N local logical readers" applies here too) -- an independent
/// session-level query from this background task would race that pump's
/// own control-stream read. Routing through CallAsync means discovery's
/// queries are also subject to LinkSelection like any other Call, which
/// is intentional, not incidental.
/// </summary>
public sealed partial class StationPool
{
    // hecate_stations.list_stations's mesh-callable procedure name. An
    // EXACT match against this (never a prefix check) is what naturally
    // excludes hecate-stations' own direct-dial-only advertisement (a
    // genuinely different procedure name, not this one with a prefix
    // stripped) -- matches macula-go's pool/discovery.go and macula-mcp's
    // mesh_list_stations tool's own filtering.
    private const string ListStationsProcedure = "hecate_stations.list_stations";

    // Mirrors DhtClient's own (private) FindRecordsByTypeProc constant --
    // duplicated as a literal, not imported, since it's genuinely trivial
    // (unlike the record-decoding logic this file DOES reuse via
    // DhtClient.RecordFromRpcValue).
    private const string FindRecordsByTypeProcedure = "_dht.find_records_by_type";

    // Matches DhtClient's own all-zero 32-byte DHT realm.
    private static readonly byte[] DhtQueryRealm = new byte[32];

    private static readonly TimeSpan DiscoveryPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Background loop spawned by Connect when StationDiscovery.Enabled.
    /// Waits for the pool's first healthy link (there's no point asking
    /// the DHT anything before then -- every call below needs one too),
    /// then resolves and calls hecate_stations.list_stations once
    /// immediately, repeating every RefreshInterval thereafter. A failed
    /// attempt (hecate_stations not currently advertised anywhere this
    /// pool's links can see, a timeout, a malformed reply) is silently
    /// retried next tick -- there's no caller to report a background-loop
    /// error to, and the bootstrap Seeds this pool was given already keep
    /// it fully usable regardless of whether discovery ever succeeds.
    ///
    /// Never runs at all under Trust.Pinned: found in adversarial review
    /// 2026-09-05 -- Pinned trust pins ONE specific node id, so ANY
    /// discovered station other than that one exact peer fails its TLS
    /// pinning check on every single dial, forever, at RespawnDelay's
    /// fixed cadence -- a permanent dial storm against up to MaxLinks-1
    /// stations for the process's whole lifetime, each occupying a slot
    /// with a link that can never become healthy. Unlike the bare-IP-
    /// under-WebPki case (AddDiscoveredLinks's own doc), there is no
    /// "maybe it works" here -- Pinned can never validate a second
    /// station's identity, so discovery is unconditionally pointless
    /// under it, not just risky.
    /// </summary>
    private async Task DiscoverStationsAsync(CancellationToken ct)
    {
        if (_trust is Trust.Pinned)
        {
            return;
        }
        if (!await WaitForAnyHealthyLinkAsync(ct).ConfigureAwait(false))
        {
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            await DiscoverOnceAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(_options.StationDiscovery.RefreshInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> WaitForAnyHealthyLinkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_links.Values.Any(l => l.Connected))
            {
                return true;
            }
            try
            {
                await Task.Delay(DiscoveryPollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    private async Task DiscoverOnceAsync(CancellationToken ct)
    {
        var realm = await ResolveListStationsRealmAsync(ct).ConfigureAwait(false);
        if (realm is null)
        {
            return;
        }
        var stations = await CallListStationsAsync(realm, ct).ConfigureAwait(false);
        if (stations is null)
        {
            return;
        }
        AddDiscoveredLinks(stations, ct);
    }

    /// <summary>
    /// Finds which realm hecate_stations.list_stations is CURRENTLY
    /// advertised under, via a DHT find_records_by_type query -- there's
    /// no way to know this without asking (it is never the default
    /// all-zero realm the query itself travels under), matching
    /// macula-go's/macula-mcp's own two-call shape: a DHT lookup, then
    /// the real call, using whatever realm the lookup found.
    /// </summary>
    private async Task<byte[]?> ResolveListStationsRealmAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DiscoveryTimeout);
            var args = Value.Map(new List<KeyValuePair<Value, Value>>
            {
                new(Value.Text("type"), Value.UInt(RecordTypes.ProcedureAdvertisement)),
            });
            var resp = await CallAsync(DhtQueryRealm, FindRecordsByTypeProcedure, args, DiscoveryTimeout, ct: cts.Token).ConfigureAwait(false);
            if (resp is not CallResponse.Result result || result.Payload is not Value.ListValue list)
            {
                return null;
            }
            foreach (var item in list.Items)
            {
                var realm = TryMatchListStationsRealm(item);
                if (realm is not null)
                {
                    return realm;
                }
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to decode one find_records_by_type reply entry as a
    /// procedure_advertisement whose procedure segment (ProcedureUri
    /// split on the FIRST "/" -- hex(realm) + "/" + procedure, matching
    /// RecordFactory.DiscoveryUri exactly) is an EXACT match for
    /// ListStationsProcedure, returning the decoded realm bytes if so.
    /// Any decode failure (wrong record type, malformed payload, no "/",
    /// bad hex, wrong-length realm) is treated as "not a match," not an
    /// error -- other stations' own advertisements are expected to be
    /// mixed into this same reply.
    /// </summary>
    private static byte[]? TryMatchListStationsRealm(Value item)
    {
        try
        {
            var rec = DhtClient.RecordFromRpcValue(item);
            if (rec.Type != RecordTypes.ProcedureAdvertisement)
            {
                return null;
            }
            var adv = RecordReading.ReadProcedureAdvertisement(rec);
            var slash = adv.ProcedureUri.IndexOf('/');
            if (slash < 0 || adv.ProcedureUri[(slash + 1)..] != ListStationsProcedure)
            {
                return null;
            }
            var realm = Convert.FromHexString(adv.ProcedureUri[..slash]);
            return realm.Length == 32 ? realm : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Calls hecate_stations.list_stations under realm with no filters (every known station), returning its raw "stations" list entries.</summary>
    private async Task<IReadOnlyList<Value>?> CallListStationsAsync(byte[] realm, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DiscoveryTimeout);
            var resp = await CallAsync(realm, ListStationsProcedure, Value.Map(Array.Empty<KeyValuePair<Value, Value>>()), DiscoveryTimeout, ct: cts.Token).ConfigureAwait(false);
            if (resp is not CallResponse.Result result || result.Payload is not Value.MapValue m)
            {
                return null;
            }
            return m.Get("stations") is Value.ListValue list ? list.Items : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds one link per station not already known, capped at
    /// StationDiscovery.MaxLinks (see its own doc for exactly what that
    /// bounds). Additive only, by construction: SpawnSeedLinkIfAbsent is
    /// already a no-op for an already-known Seed, and there is
    /// deliberately no removal path here at all.
    ///
    /// Two rejections beyond DialTargetFromStationRow's own:
    ///  1. A row whose node_id already matches a link this pool holds
    ///     (bootstrap or previously discovered) is skipped, keyed by the
    ///     PEER'S OWN IDENTITY rather than by host:port spelling -- a
    ///     bootstrap Seed spelled differently than hecate_stations' own
    ///     node_record.hostname for the SAME station (different case, a
    ///     CNAME, an IP-literal seed) would otherwise pass Seed-based
    ///     dedupe and add a SECOND connection to a station this pool
    ///     already holds under one shared identity, which the station's
    ///     own per-identity dedupe answers by kicking the older link --
    ///     a permanent flap. Residual window, not closed by this: a link
    ///     only has a LastKnownNodeId after its FIRST successful
    ///     handshake, so a bootstrap Seed still mid-handshake is
    ///     invisible to HasLinkForNodeId until it finishes.
    ///  2. A row whose only dialable host is a bare IP literal (no
    ///     hostname, only host_advertised) is skipped when Trust is
    ///     WebPki -- see DialTargetFromStationRow's own doc for why
    ///     host_advertised is IP-only on the real fleet. Adding it
    ///     anyway would occupy a MaxLinks slot with a link that can
    ///     NEVER become healthy under WebPki (TLS cert validation fails
    ///     on every single redial, forever, at RespawnDelay's fixed
    ///     cadence, with nothing here to give up and free the slot).
    ///     Insecure/Pinned trust modes may genuinely support a bare IP,
    ///     so this only rejects under WebPki specifically.
    /// </summary>
    private void AddDiscoveredLinks(IReadOnlyList<Value> stations, CancellationToken ct)
    {
        var isWebPki = _trust is Trust.WebPki;
        foreach (var station in stations)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            if (_links.Count >= _options.StationDiscovery.MaxLinks)
            {
                break;
            }
            if (!DialTargetFromStationRow(station, out var host, out var port))
            {
                continue;
            }
            if (isWebPki && System.Net.IPAddress.TryParse(host, out _))
            {
                continue;
            }
            if (TryStationNodeId(station, out var nodeId) && HasLinkForNodeId(nodeId))
            {
                continue;
            }
            SpawnSeedLinkIfAbsent(new Seed(host, port));
        }
    }

    /// <summary>
    /// Adds and spawns a link for seed if it isn't already one of this
    /// pool's known Seeds -- TryAdd is atomic, so a race against another
    /// discovery tick (there is only ever one DiscoverStationsAsync task
    /// per pool, but this keeps the same safety Connect's own bootstrap
    /// loop gets from constructing the whole dictionary up front) can't
    /// double-spawn a link for the same Seed.
    /// </summary>
    private void SpawnSeedLinkIfAbsent(Seed seed)
    {
        // Ordinal reserved via Interlocked BEFORE TryAdd, not after: two
        // links must never share an ordinal even if TryAdd then fails
        // (harmless -- a reserved-but-unused ordinal just leaves a gap,
        // not a collision) -- see PooledLink.Ordinal's own doc for why
        // this field exists at all.
        var link = new PooledLink(seed, Interlocked.Increment(ref _nextLinkOrdinal));
        if (_links.TryAdd(seed, link))
        {
            SpawnLinkTasks(link);
        }
    }

    /// <summary>
    /// Reports whether any link this pool currently holds (bootstrap or
    /// previously discovered, live or mid-backoff) has ever proved this
    /// exact peer node id.
    /// </summary>
    internal bool HasLinkForNodeId(byte[] nodeId) => HasLinkForNodeIdCore(_links.Values, nodeId);

    /// <summary>
    /// The pure logic behind HasLinkForNodeId, extracted as a static
    /// function taking a plain link sequence so it's directly
    /// unit-testable with hand-constructed PooledLinks, without a live
    /// StationPool (see SelectLinksCore's own doc on why this repo has
    /// no fake-dialer seam for that).
    /// </summary>
    internal static bool HasLinkForNodeIdCore(IEnumerable<PooledLink> links, byte[] nodeId) =>
        links.Any(l => l.LastKnownNodeId is { } known && known.AsSpan().SequenceEqual(nodeId));

    /// <summary>
    /// Extracts a station row's node_id (32 raw bytes, same CBOR-bytes
    /// wire convention as every other identifier field in this
    /// response).
    /// </summary>
    internal static bool TryStationNodeId(Value station, out byte[] nodeId)
    {
        nodeId = Array.Empty<byte>();
        if (station is not Value.MapValue m || m.Get("node_id") is not { } v)
        {
            return false;
        }
        try
        {
            var b = v.AsBytes();
            if (b.Length != 32)
            {
                return false;
            }
            nodeId = b;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts a dialable host:port from one
    /// hecate_stations.list_stations response row, preferring the
    /// node_record-derived hostname (a real DNS name, e.g.
    /// "station-de-frankfurt.macula.io") over host_advertised[0] (the
    /// station_endpoint-derived field DirectDial.ResolveStationEndpointAsync
    /// dials by).
    ///
    /// This is the OPPOSITE priority from ResolveStationEndpointAsync,
    /// deliberately: confirmed live against the real fleet
    /// (mesh_list_stations, 2026-09-05, cross-checked with macula-go's
    /// identical port) that host_advertised there is ALWAYS a bare IPv6
    /// literal, never a DNS name, on every single station row.
    /// DirectDial can dial that safely because it dials under
    /// Trust.Insecure and verifies trust by matching the resolved
    /// station's NODE ID instead -- it never depends on the peer's TLS
    /// certificate covering the address dialed. This pool's links dial
    /// under whatever Trust the caller configured (WebPki in any real
    /// deployment), which fails immediately on a bare IP ("doesn't
    /// contain any IP SANs"). hostname is what each station's own cert
    /// is actually issued for, so it's the correct preference here even
    /// though it's the LOWER-priority field for direct-dial's own,
    /// differently-trusted use case. A row with neither field at all is
    /// skipped, not guessed at.
    /// </summary>
    internal static bool DialTargetFromStationRow(Value station, out string host, out int port)
    {
        host = "";
        port = 0;
        if (station is not Value.MapValue m || m.Get("quic_port") is not { } portV)
        {
            return false;
        }
        long portI;
        try
        {
            portI = portV.AsInt();
        }
        catch (Exception)
        {
            return false;
        }
        if (portI <= 0 || portI > 65535)
        {
            return false;
        }

        if (m.Get("hostname") is { } hostnameV && TryAsHostText(hostnameV, out var hostname))
        {
            host = hostname;
            port = (int)portI;
            return true;
        }
        if (m.Get("host_advertised") is Value.ListValue { Items.Count: > 0 } hosts && TryAsHostText(hosts.Items[0], out var advertised))
        {
            host = advertised;
            port = (int)portI;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Decodes a host field, trying bytes then text (host_advertised
    /// and hostname both arrive as CBOR byte strings on the wire, not
    /// text, per macula_record.erl's with_host_list/2 -- confirmed
    /// live). An empty string is rejected either way: a present-but-
    /// empty field is not a usable dial target, and
    /// IPAddress.TryParse("") returns false, so an unrejected empty
    /// host would silently bypass AddDiscoveredLinks's own
    /// bare-IP-under-WebPki check.
    /// </summary>
    private static bool TryAsHostText(Value v, out string host)
    {
        host = "";
        try
        {
            var s = v.AsText();
            if (s.Length == 0)
            {
                return false;
            }
            host = s;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
