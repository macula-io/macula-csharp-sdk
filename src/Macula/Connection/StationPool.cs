using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;

namespace Macula.Connection;

/// <summary>Delivered to a subscriber for each inbound EVENT the pool has not already delivered from another link.</summary>
public delegate Task PoolEventHandler(EventInfo eventInfo);

/// <summary>One station a <see cref="StationPool"/> dials -- a plain host:port, no identity attached (every link in a pool shares the pool's one identity).</summary>
public sealed record Seed(string Host, int Port);

/// <summary>Snapshot of one configured seed's current link. `NodeId` is null until CONNECT/HELLO completes.</summary>
public sealed record LinkInfo(Seed Seed, bool Connected, byte[]? NodeId);

/// <summary>Aggregate health snapshot of a pool. Lock-free best-effort read -- suitable for a `/health` endpoint, not hot-loop polling.</summary>
public sealed record PoolStatus(int HealthyLinks, int FailedLinks, int Subscriptions);

/// <summary>No link in the pool has completed its CONNECT/HELLO handshake. Mirrors macula_client's `{error, no_healthy_station}`.</summary>
public sealed class NoHealthyStationException : Exception
{
    public NoHealthyStationException()
        : base("no link in the pool has completed its CONNECT/HELLO handshake")
    {
    }
}

/// <summary>Every link targeted by a publish rejected it. Mirrors macula_client's `summarize_publish` all-failed case.</summary>
public sealed class PublishFailedException : Exception
{
    public PublishFailedException(string message) : base(message)
    {
    }
}

/// <summary>Tunables for <see cref="StationPool"/>. Defaults match macula_client.erl's own.</summary>
public sealed class StationPoolOptions
{
    /// <summary>How many currently-connected links a single publish fans out to. Partial success counts as success. Default 1.</summary>
    public int ReplicationFactor { get; init; } = 1;

    /// <summary>Flat delay before redialing a seed after its link dies (or fails to dial in the first place). Default 1s, matching ?LINK_RESPAWN_DELAY_MS -- flat, not exponential, per the reference and the rest of this SDK family's non-TS ports.</summary>
    public TimeSpan RespawnDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-link CONNECT/HELLO deadline. Default 30s, matching Session.ConnectAsync's own default.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Inbound-EVENT dedup window. Default 60s.</summary>
    public TimeSpan DedupWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the dedup table is swept for entries older than <see cref="DedupWindow"/>. Default 30s.</summary>
    public TimeSpan DedupSweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on a dying link's GOODBYE-send before respawn proceeds
    /// anyway. Session.CloseAsync has no timeout of its own; on an
    /// already-half-dead connection its GOODBYE write can block up to the
    /// QUIC idle timeout, which would silently turn a "flat 1s backoff"
    /// into "1s plus however long that takes." Default 2s.
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on any single wire-level write issued through a link's
    /// send gate outside an application call (replay, Subscribe/Advertise/
    /// Unadvertise). Matters most during respawn's replay step, which runs
    /// under the pool's single state lock -- a stall there would block
    /// every OTHER Subscribe/Advertise/Publish/Call across the whole pool,
    /// not just this one link, for as long as the write hangs. Default 5s.
    /// </summary>
    public TimeSpan WireWriteTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// N concurrently-dialed links to N seed stations, all under one shared
/// identity (stations see the pool as a single peer, matching
/// macula_client.erl's own design). A dead link respawns after a flat
/// backoff and replays every tracked subscription/advertisement onto the
/// fresh connection. Inbound EVENTs are deduped across links before local
/// delivery. Port of macula_client.erl (macula-io/macula, src/client/) --
/// see that module's own doc for the design this mirrors.
///
/// == Why a pump, not Session's own Call/RecvEvent/ServeOneCall ==
///
/// <see cref="Session"/>'s control-stream methods are documented as "one
/// thing at a time": CallAsync, RecvEventAsync and ServeOneCallGatedAsync
/// each directly await the next frame off the SAME stream, and would
/// steal each other's frames if run concurrently on one Session. A pool
/// link needs all three roles at once (deliver EVENTs to subscribers,
/// serve inbound CALLs against advertised procedures, correlate RESULT/
/// ERROR replies to this pool's own outbound calls) over ONE shared
/// connection -- opening a second Session to the same seed under the
/// pool's shared identity gets the whole link kicked (a station drops a
/// connection the instant a second one arrives under the same identity).
///
/// So each <see cref="PooledLink"/> runs a single background pump Task
/// that is the ONLY reader of its Session's control stream, and demuxes
/// each frame by type: EVENT -> dedup then fan out to subscribers; CALL ->
/// dispatch to the registered handler (off-pump -- see below); RESULT/
/// ERROR -> complete the matching pending call. This is .NET's mailbox
/// equivalent for this specific problem (one physical stream, N local
/// logical readers, no stolen frames) -- a single-reader pump plus
/// Channels/ConcurrentDictionary/TaskCompletionSource, not a shortcut
/// under-delivering relative to Erlang's per-connection process mailbox.
///
/// Handler dispatch for an inbound CALL is spawned OFF the pump loop, never
/// awaited inline -- awaiting it inline is exactly the
/// macula_link_inline_handler_deadlock shape macula itself shipped and
/// fixed in 10.18.0 (macula_station_link.erl spawns a child per CALL for
/// the same reason): a handler that calls back through this same link
/// would otherwise deadlock waiting for a RESULT the pump can never read,
/// because the pump is the one blocked awaiting the handler.
///
/// == What is NOT ported this pass (documented, not silent) ==
///
/// No per-publisher `ordered` reorder buffer (macula_pubsub_order.erl):
/// two links race independently, so cross-link delivery order for one
/// publisher is not guaranteed. Delivery is as-arrives only.
///
/// Advertised STREAMING procedures get wire-level ADVERTISE replay (so a
/// provider's station-side registration survives a respawn) but the pool
/// does not run its own STREAM_OPEN accept-and-dispatch loop -- use
/// <see cref="PickConnectedSession"/> to get a live Session and drive
/// <see cref="Streaming.StreamHandle"/> against it directly, exactly as a
/// caller would against a single Session today. A dedicated stream is a
/// separate QUIC stream from the control stream the pump owns, so this is
/// safe to do concurrently with the pump.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class StationPool : IAsyncDisposable
{
    private readonly KeyPair _identity;
    private readonly Trust _trust;
    private readonly StationPoolOptions _options;
    private readonly Dictionary<Seed, PooledLink> _links;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly ConcurrentDictionary<(string RealmHex, string Topic), ConcurrentDictionary<Guid, PoolEventHandler>> _subs = new();
    private readonly ConcurrentDictionary<Guid, (string RealmHex, string Topic)> _subIndex = new();
    private readonly ConcurrentDictionary<(string RealmHex, string Procedure), (CallHandler Handler, Policy Policy)> _procs = new();
    private readonly ConcurrentDictionary<(string RealmHex, string Procedure), byte> _streamProcs = new();
    private readonly EventDedup _dedup;
    private readonly CancellationTokenSource _poolCts = new();
    private readonly List<Task> _linkTasks = new();
    private long _publishSeq;
    private Timer? _dedupSweepTimer;
    private bool _disposed;

    public KeyPair Identity => _identity;

    private StationPool(KeyPair identity, Trust trust, StationPoolOptions options, IReadOnlyList<Seed> seeds)
    {
        _identity = identity;
        _trust = trust;
        _options = options;
        _dedup = new EventDedup(options.DedupWindow);
        _links = seeds.Distinct().ToDictionary(s => s, s => new PooledLink(s));
        // Wall-clock-seeded, matching macula_client.erl's init/1: a
        // restart under a persisted identity must not re-issue seqs a
        // station's own dedup window from the pre-restart tail would
        // still be holding.
        _publishSeq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    }

    /// <summary>
    /// Spawn a pool with one link per seed. Returns as soon as every
    /// link's dial has STARTED, not once any is connected -- handshakes
    /// complete asynchronously, matching macula_client:connect/2.
    /// </summary>
    public static StationPool Connect(IReadOnlyList<Seed> seeds, Trust trust, KeyPair? identity = null, StationPoolOptions? options = null)
    {
        if (seeds.Count == 0)
        {
            throw new ArgumentException("at least one seed is required", nameof(seeds));
        }

        var pool = new StationPool(identity ?? KeyPair.GenerateWithDefaultPuzzle(), trust, options ?? new StationPoolOptions(), seeds);
        foreach (var link in pool._links.Values)
        {
            pool._linkTasks.Add(Task.Run(() => pool.RunLinkAsync(link, pool._poolCts.Token)));
        }
        pool._dedupSweepTimer = new Timer(_ => pool._dedup.Sweep(), null, pool._options.DedupSweepInterval, pool._options.DedupSweepInterval);
        return pool;
    }

    /// <summary>
    /// Publish to <see cref="StationPoolOptions.ReplicationFactor"/>
    /// currently-connected links (default 1). Partial success = success.
    /// Throws <see cref="NoHealthyStationException"/> if no link is
    /// connected, or <see cref="PublishFailedException"/> if every
    /// targeted link rejected the frame.
    /// </summary>
    public async Task PublishAsync(byte[] realm, string topic, Value payload, CancellationToken ct = default)
    {
        List<PooledLink> connected;
        long seq;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            connected = ConnectedLinksSnapshot();
            seq = _publishSeq++;
        }
        finally
        {
            _stateLock.Release();
        }

        if (connected.Count == 0)
        {
            throw new NoHealthyStationException();
        }

        var n = Math.Clamp(_options.ReplicationFactor, 0, connected.Count);
        var targets = connected.Take(n).ToList();
        if (targets.Count == 0)
        {
            return; // replication factor capped at 0 by config -- a no-op, not a failure.
        }

        var spec = new PublishSpec
        {
            Topic = topic,
            Realm = realm,
            Publisher = _identity.NodeId(),
            Seq = (ulong)seq,
            Payload = payload,
            PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var results = await Task.WhenAll(targets.Select(link => TryPublishAsync(link, spec, ct))).ConfigureAwait(false);
        if (!results.Any(ok => ok))
        {
            throw new PublishFailedException("every targeted link rejected the publish");
        }
    }

    private static async Task<bool> TryPublishAsync(PooledLink link, PublishSpec spec, CancellationToken ct)
    {
        try
        {
            await link.PublishAsync(spec, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation, not a link rejecting the write --
            // let it propagate through Task.WhenAll rather than being
            // folded into "every targeted link rejected the publish."
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Subscribe to (realm, topic). The pool wire-subscribes every
    /// currently-connected link the FIRST time a given (realm, topic) is
    /// tracked, and replays it onto every link that respawns afterward.
    /// Returns a subscription id for <see cref="UnsubscribeAsync"/>.
    /// </summary>
    public async Task<Guid> SubscribeAsync(byte[] realm, string topic, PoolEventHandler handler, CancellationToken ct = default)
    {
        var subId = Guid.NewGuid();
        var key = (ToHex(realm), topic);
        bool isNewTopic;
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            isNewTopic = !_subs.ContainsKey(key);
            var byId = _subs.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, PoolEventHandler>());
            byId[subId] = handler;
            _subIndex[subId] = key;
            connected = isNewTopic ? ConnectedLinksSnapshot() : new List<PooledLink>();
        }
        finally
        {
            _stateLock.Release();
        }

        if (isNewTopic)
        {
            var spec = new SubscribeSpec { Topic = topic, Realm = realm, Subscriber = _identity.NodeId() };
            foreach (var link in connected)
            {
                await TrySendReplayAsync(link, ct, (s, c) => s.SubscribeAsync(spec, c)).ConfigureAwait(false);
            }
        }

        return subId;
    }

    /// <summary>
    /// Drop a subscription. Idempotent. Matching macula_client.erl's own
    /// unsubscribe/2 exactly: the wire-level SUBSCRIBE is NOT retracted --
    /// it persists for the pool's lifetime, multiplexed across whichever
    /// local subscribers remain tracked for that (realm, topic).
    /// </summary>
    public async Task UnsubscribeAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_subIndex.TryRemove(subscriptionId, out var key))
            {
                return;
            }
            if (_subs.TryGetValue(key, out var byId))
            {
                byId.TryRemove(subscriptionId, out _);
                if (byId.IsEmpty)
                {
                    _subs.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Register a handler for (realm, procedure) on every currently-live
    /// link, and replay it onto every link that respawns afterward. Unlike
    /// <see cref="SubscribeAsync"/>, this always fans out on every call
    /// (matching macula_client.erl's own advertise/5), not just the first
    /// time this (realm, procedure) is seen.
    /// </summary>
    public async Task AdvertiseAsync(byte[] realm, string procedure, CallHandler handler, Policy? policy = null, CancellationToken ct = default)
    {
        var key = (ToHex(realm), procedure);
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _procs[key] = (handler, policy ?? Policy.Open);
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        var spec = new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = _identity.NodeId() };
        foreach (var link in connected)
        {
            await TrySendReplayAsync(link, ct, (s, c) => s.AdvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    /// <summary>Drop a previously-advertised procedure on every live link and remove it from replay state. Idempotent.</summary>
    public async Task UnadvertiseAsync(byte[] realm, string procedure, CancellationToken ct = default)
    {
        var key = (ToHex(realm), procedure);
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _procs.TryRemove(key, out _);
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        var spec = new UnadvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = _identity.NodeId() };
        foreach (var link in connected)
        {
            await TrySendReplayAsync(link, ct, (s, c) => s.UnadvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wire-level ADVERTISE replay only for a streaming procedure -- see
    /// this class's own doc for why the pool does not run a STREAM_OPEN
    /// accept loop itself. Use <see cref="PickConnectedSession"/> plus
    /// <see cref="Streaming.StreamHandle.AcceptAsync"/> to actually serve
    /// inbound streams for this procedure.
    /// </summary>
    public async Task AdvertiseStreamAsync(byte[] realm, string procedure, CancellationToken ct = default)
    {
        var key = (ToHex(realm), procedure);
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _streamProcs[key] = 0;
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        var spec = new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = _identity.NodeId() };
        foreach (var link in connected)
        {
            await TrySendReplayAsync(link, ct, (s, c) => s.AdvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    public async Task UnadvertiseStreamAsync(byte[] realm, string procedure, CancellationToken ct = default)
    {
        var key = (ToHex(realm), procedure);
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _streamProcs.TryRemove(key, out _);
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        var spec = new UnadvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = _identity.NodeId() };
        foreach (var link in connected)
        {
            await TrySendReplayAsync(link, ct, (s, c) => s.UnadvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issue a CALL, trying each currently-connected link in turn. A
    /// wire-level RESULT short-circuits immediately; a wire-level ERROR or
    /// a transport failure moves to the next link UNLESS it was the last
    /// one, in which case that outcome is what's returned/thrown -- mirrors
    /// macula_client:call_first_success/5 exactly, including its choice to
    /// surface the LAST attempt's own outcome rather than a generic
    /// failure when every link was actually tried.
    /// </summary>
    public async Task<CallResponse> CallAsync(byte[] realm, string procedure, Value payload, TimeSpan timeout, byte[]? ucanToken = null, CancellationToken ct = default)
    {
        List<PooledLink> connected;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        if (connected.Count == 0)
        {
            throw new NoHealthyStationException();
        }

        for (var i = 0; i < connected.Count; i++)
        {
            var isLast = i == connected.Count - 1;
            try
            {
                var resp = await CallOnLinkAsync(connected[i], realm, procedure, payload, timeout, ucanToken ?? Array.Empty<byte>(), ct).ConfigureAwait(false);
                if (resp is CallResponse.Result || isLast)
                {
                    return resp;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Genuine caller cancellation, as opposed to CallOnLinkAsync's
                // own per-link timeout (which it converts to a TimeoutException
                // before it ever reaches here) -- never retry across links on
                // this, unlike a per-link failure.
                throw;
            }
            catch (Exception) when (!isLast)
            {
                // A per-link timeout or transport-level failure -- try the next one.
            }
        }

        throw new NoHealthyStationException();
    }

    /// <summary>
    /// The whole operation (send, RPC-telemetry writes, and the wait for a
    /// reply) is bounded by ONE deadline derived from <paramref name="timeout"/>,
    /// not just the reply-wait -- a caller with a 5s budget must not be able
    /// to block indefinitely on <see cref="PooledLink.SendGatedAsync"/> if
    /// some other write (e.g. a slow inbound-CALL reply) is holding the
    /// link's send gate. `boundedCt` firing from the deadline (as opposed to
    /// from <paramref name="ct"/> itself) is converted to
    /// <see cref="TimeoutException"/>, mirroring the same
    /// `OperationCanceledException) when (!ct.IsCancellationRequested)`
    /// pattern <see cref="Session.CallAsync"/> already uses for the
    /// identical reason.
    /// </summary>
    private async Task<CallResponse> CallOnLinkAsync(PooledLink link, byte[] realm, string procedure, Value payload, TimeSpan timeout, byte[] ucanToken, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var boundedCt = timeoutCts.Token;

        var callId = new byte[16];
        Random.Shared.NextBytes(callId);
        var key = Convert.ToHexStringLower(callId);
        var tcs = new TaskCompletionSource<CallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.PendingCalls[key] = tcs;

        var requestId = RpcFacts.RandomRequestId();
        await RpcFacts.AnnounceSentAsync(link, realm, _identity, requestId, boundedCt).ConfigureAwait(false);

        var spec = new CallSpec
        {
            CallId = callId,
            Procedure = procedure,
            Realm = realm,
            Payload = payload,
            DeadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)timeout.TotalMilliseconds,
            Caller = _identity.NodeId(),
            UcanToken = ucanToken,
        };

        CallResponse? resp = null;
        Exception? err = null;
        try
        {
            await link.SendGatedAsync((s, c) => s.SendAsync(CallFrame.Build(spec), c), boundedCt).ConfigureAwait(false);
            resp = await tcs.Task.WaitAsync(boundedCt).ConfigureAwait(false);
            return resp;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            err = new TimeoutException($"no response for call_id {key} within {timeout}");
            throw err;
        }
        catch (Exception e)
        {
            err = e;
            throw;
        }
        finally
        {
            link.PendingCalls.TryRemove(key, out _);
            await RpcFacts.AnnounceCompletedAsync(link, realm, _identity, requestId, resp, err, boundedCt).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A live Session from a currently-connected link, for driving
    /// dedicated-stream operations (<see cref="Streaming.StreamHandle"/>,
    /// content transfer) directly against a pooled connection. Safe to use
    /// concurrently with the pool's own pump -- a dedicated stream is a
    /// separate QUIC stream from the control stream the pump reads.
    /// Do NOT call Session.CallAsync/RecvEventAsync/ServeOneCallGatedAsync
    /// on the returned Session -- those read the control stream the pump
    /// already owns and would race it. Returns null if no link is
    /// currently connected.
    /// </summary>
    public Session? PickConnectedSession() => _links.Values.FirstOrDefault(l => l.Connected)?.Session;

    /// <summary>Aggregate health snapshot. Lock-free best-effort -- not for hot-loop polling.</summary>
    public PoolStatus Status
    {
        get
        {
            var healthy = _links.Values.Count(l => l.Connected);
            return new PoolStatus(healthy, _links.Count - healthy, _subIndex.Count);
        }
    }

    /// <summary>Per-seed snapshot. Lock-free best-effort -- not for hot-loop polling.</summary>
    public IReadOnlyList<LinkInfo> Links =>
        _links.Values.Select(l => new LinkInfo(l.Seed, l.Connected, l.Connected ? l.Session?.RemoteInfo.NodeId : null)).ToList();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _dedupSweepTimer?.Dispose();
        _poolCts.Cancel();
        await Task.WhenAll(_linkTasks).ConfigureAwait(false);
        _stateLock.Dispose();
        _poolCts.Dispose();
    }

    //====================================================================
    // Link lifecycle
    //====================================================================

    private async Task RunLinkAsync(PooledLink link, CancellationToken poolCt)
    {
        while (!poolCt.IsCancellationRequested)
        {
            Session session;
            try
            {
                session = await Session.ConnectAsync(link.Seed.Host, link.Seed.Port, _identity, _trust, _options.HandshakeTimeout, poolCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (poolCt.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Dial failure -- first attempt or a later one, same
                // respawn cycle either way (mirrors after_link_start's
                // error path in the reference, which schedules a respawn
                // timer even for the very first dial).
                await DelayRespawnAsync(poolCt).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _stateLock.WaitAsync(poolCt).ConfigureAwait(false);
                try
                {
                    link.Session = session;
                    await ReplayOntoAsync(link, poolCt).ConfigureAwait(false);
                    link.Connected = true;
                }
                finally
                {
                    _stateLock.Release();
                }
            }
            catch (OperationCanceledException) when (poolCt.IsCancellationRequested)
            {
                // link.Connected/Session were never flipped to reflect this
                // session on the pool-shutdown path (the state lock wait
                // itself was what got cancelled), so there is nothing to
                // unmark -- just close it and stop.
                await CloseSessionAsync(session).ConfigureAwait(false);
                return;
            }

            try
            {
                await PumpAsync(link, session, poolCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (poolCt.IsCancellationRequested)
            {
                // Pool shutdown, not a fault -- still unmark the link (a
                // caller mid-CallAsync against it, or Status/Links read
                // after DisposeAsync returns, must not see a stale
                // Connected=true) before closing.
                await MarkDisconnectedAsync(link).ConfigureAwait(false);
                await CloseSessionAsync(session).ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                // Transport-level fault on the control stream -- fall
                // through to respawn. A tolerable per-frame issue (an
                // unrecognized frame type, e.g. the station's own
                // unprompted content-procedure advertise broadcasts)
                // never reaches here -- PumpAsync only lets a raw
                // RecvAsync failure propagate, never a parse mismatch.
            }

            await MarkDisconnectedAsync(link).ConfigureAwait(false);
            await CloseSessionAsync(session).ConfigureAwait(false);
            await DelayRespawnAsync(poolCt).ConfigureAwait(false);
        }
    }

    /// <summary>Flip a link to disconnected and fail its in-flight calls immediately, rather than letting them wait out their own timeout against a link that's already known to be gone.</summary>
    private async Task MarkDisconnectedAsync(PooledLink link)
    {
        await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            link.Connected = false;
            link.Session = null;
        }
        finally
        {
            _stateLock.Release();
        }
        FailPendingCalls(link, new IOException($"link to {link.Seed.Host}:{link.Seed.Port} disconnected"));
    }

    /// <summary>
    /// Runs with `_stateLock` held (called only from RunLinkAsync's connect
    /// branch). Replays every tracked subscription and advertisement onto
    /// `link`'s freshly-connected Session -- first connect and every later
    /// respawn go through this exact same path, so there is no separate
    /// "pre-handshake pending" concept to keep in sync: a Subscribe/
    /// Advertise call that lands while this link is still dialing simply
    /// registers in `_subs`/`_procs` under the same lock this method
    /// itself needs, so it is either already in the snapshot this method
    /// replays, or (if it lands after) picked up by SubscribeAsync's own
    /// immediate wire-issue to already-connected links -- never both,
    /// never neither, because both paths serialize through `_stateLock`.
    /// </summary>
    private async Task ReplayOntoAsync(PooledLink link, CancellationToken ct)
    {
        foreach (var key in _subs.Keys.ToList())
        {
            var (realmHex, topic) = key;
            var spec = new SubscribeSpec { Topic = topic, Realm = FromHex(realmHex), Subscriber = _identity.NodeId() };
            await TrySendReplayAsync(link, ct, (s, c) => s.SubscribeAsync(spec, c)).ConfigureAwait(false);
        }
        foreach (var key in _procs.Keys.ToList())
        {
            var (realmHex, procedure) = key;
            var spec = new AdvertiseSpec { Realm = FromHex(realmHex), Procedure = procedure, Advertiser = _identity.NodeId() };
            await TrySendReplayAsync(link, ct, (s, c) => s.AdvertiseAsync(spec, c)).ConfigureAwait(false);
        }
        foreach (var key in _streamProcs.Keys.ToList())
        {
            var (realmHex, procedure) = key;
            var spec = new AdvertiseSpec { Realm = FromHex(realmHex), Procedure = procedure, Advertiser = _identity.NodeId() };
            await TrySendReplayAsync(link, ct, (s, c) => s.AdvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A write's failure is swallowed, matching macula_client_replay.erl
    /// exactly for a REPLAY write: this link's own next respawn retries.
    /// Also used for a live (non-replay) wire-issue from Subscribe/
    /// Advertise/Unadvertise -- same swallow policy there too, since those
    /// are already fire-and-forget on the wire and the pool's own tracked
    /// state (which is what actually matters) was already updated before
    /// this runs.
    ///
    /// Bounded by <see cref="StationPoolOptions.WireWriteTimeout"/>, NOT
    /// just `ct`: called from ReplayOntoAsync while `_stateLock` is held,
    /// so an unbounded stall here would block every other pool operation,
    /// not just this one link. A timeout here is swallowed like any other
    /// write failure; only genuine cancellation via the ORIGINAL `ct`
    /// propagates.
    /// </summary>
    private async Task TrySendReplayAsync(PooledLink link, CancellationToken ct, Func<Session, CancellationToken, Task> send)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.WireWriteTimeout);
        try
        {
            await link.SendGatedAsync(send, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // swallowed -- see summary.
        }
    }

    private async Task DelayRespawnAsync(CancellationToken poolCt)
    {
        try
        {
            await Task.Delay(_options.RespawnDelay, poolCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // pool disposing -- the outer loop's own poolCt check exits it.
        }
    }

    private async Task CloseSessionAsync(Session session)
    {
        using var cts = new CancellationTokenSource(_options.CloseTimeout);
        try
        {
            await session.CloseAsync().AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort and bounded -- matches Session.CloseAsync's own
            // GOODBYE-send tolerance, just capped so a hung close can't
            // turn "flat backoff" into "backoff plus the QUIC idle
            // timeout." A dead identity must never dial its replacement
            // before this returns -- see this class's own doc on the
            // same-identity-double-connect hazard.
        }
    }

    private static void FailPendingCalls(PooledLink link, Exception reason)
    {
        foreach (var key in link.PendingCalls.Keys.ToList())
        {
            if (link.PendingCalls.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(reason);
            }
        }
    }

    private List<PooledLink> ConnectedLinksSnapshot() => _links.Values.Where(l => l.Connected).ToList();

    //====================================================================
    // Pump -- the sole reader of a link's control stream
    //====================================================================

    /// <summary>
    /// EVENT frames go through a per-link, single-consumer Channel rather
    /// than a bare `Task.Run` per frame: dispatching each EVENT to its own
    /// fire-and-forget Task let the threadpool reorder them relative to
    /// arrival, and let the SAME subscriber's handler be re-entered
    /// concurrently for two events that arrived a moment apart on this
    /// link -- neither matches the reference (one link's frames arrive in
    /// strict order off one QUIC stream; a gen_server-owned mailbox
    /// delivers to a subscriber pid one message at a time). A single
    /// dedicated consumer task drains the channel strictly in receipt
    /// order, awaiting each event's full dedup+fan-out before starting the
    /// next -- ordering and non-reentrancy are per link, matching the
    /// bound this class's own doc already states (no CROSS-link ordering
    /// is attempted; that would need the reorder buffer this pass
    /// deliberately doesn't port).
    ///
    /// The channel write itself is a fast, non-blocking TryWrite (unbounded
    /// channel), so the pump loop's own promptness at reading CALL/RESULT/
    /// ERROR frames is unaffected by how quickly the event consumer keeps up.
    /// </summary>
    private async Task PumpAsync(PooledLink link, Session session, CancellationToken poolCt)
    {
        var events = Channel.CreateUnbounded<Value>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
        var eventConsumer = Task.Run(() => ConsumeEventsAsync(events.Reader, poolCt), CancellationToken.None);
        try
        {
            while (true)
            {
                var frame = await session.RecvAsync(poolCt).ConfigureAwait(false);
                switch (FrameType(frame))
                {
                    case "event":
                        events.Writer.TryWrite(frame);
                        break;
                    case "call":
                        _ = Task.Run(() => HandleInboundCallAsync(link, frame, poolCt), CancellationToken.None);
                        break;
                    case "result":
                    case "error":
                        CompletePendingCall(link, frame);
                        break;
                    default:
                        // Tolerated, not fatal -- e.g. the live station's own
                        // unprompted advertise broadcasts for its built-in
                        // _content.* procedures, periodically sent on every
                        // connected client's control stream.
                        break;
                }
            }
        }
        finally
        {
            // No more EVENTs will arrive for this (dying or pool-closing)
            // link. The consumer drains whatever is already buffered, in
            // order, then exits on its own -- not awaited here, so a slow
            // straggler event doesn't delay RunLinkAsync's own respawn
            // cycle.
            events.Writer.TryComplete();
            _ = eventConsumer;
        }
    }

    private async Task ConsumeEventsAsync(ChannelReader<Value> reader, CancellationToken poolCt)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(poolCt).ConfigureAwait(false))
            {
                await HandleEventAsync(frame, poolCt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Pool disposing -- whatever is still buffered is abandoned.
        }
    }

    private static string FrameType(Value frame) =>
        frame is Value.MapValue map && map.Get("frame_type") is Value.TextValue t ? t.AsText() : "";

    private async Task HandleEventAsync(Value frame, CancellationToken ct)
    {
        EventInfo evt;
        try
        {
            evt = EventFrameParsing.Parse(frame);
        }
        catch (ParseFrameException)
        {
            return;
        }

        if (!_dedup.CheckNew(evt.Realm, evt.Publisher, evt.Seq, evt.Topic))
        {
            return; // another link already delivered this fact.
        }

        if (!_subs.TryGetValue((ToHex(evt.Realm), evt.Topic), out var byId))
        {
            return;
        }

        foreach (var handler in byId.Values)
        {
            try
            {
                await handler(evt).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // one subscriber's failure must not affect delivery to
                // others, or this link's own pump.
            }
        }
    }

    private void CompletePendingCall(PooledLink link, Value frame)
    {
        var callId = CallFrameParsing.FrameCallId(frame);
        if (callId is null)
        {
            return;
        }
        if (!link.PendingCalls.TryRemove(Convert.ToHexStringLower(callId), out var tcs))
        {
            return; // unmatched (stale, foreign, or already timed out and removed) -- ignore.
        }
        try
        {
            tcs.TrySetResult(CallFrameParsing.ParseCallResponse(frame));
        }
        catch (ParseFrameException e)
        {
            tcs.TrySetException(e);
        }
    }

    /// <summary>
    /// Off-pump: the pump loop only ever reads frames and dispatches, it
    /// never awaits handler logic itself -- see this class's own doc on
    /// why (macula_link_inline_handler_deadlock).
    /// </summary>
    private async Task HandleInboundCallAsync(PooledLink link, Value frame, CancellationToken ct)
    {
        CallInfo callInfo;
        try
        {
            callInfo = CallFrameParsing.ParseCall(frame);
        }
        catch (ParseFrameException)
        {
            return;
        }

        // Read _procs exactly ONCE, so the policy check and the handler
        // dispatch that follows it always agree on the same registration --
        // two independent TryGetValue calls here would let a concurrent
        // AdvertiseAsync that TIGHTENS the policy (e.g. Open -> Required)
        // land in between them: the policy check would pass against the
        // stale Open entry while the handler that runs is already the new
        // one, serving an unauthenticated caller a procedure that had
        // already been re-gated. A single snapshot closes that window.
        var snapshot = _procs.TryGetValue((ToHex(callInfo.Realm), callInfo.Procedure), out var entry) ? entry : ((CallHandler Handler, Policy Policy)?)null;
        CallLookup lookup = (_, _) => snapshot?.Handler;
        PolicyLookup policyLookup = (_, _) => snapshot?.Policy ?? Policy.Open;

        var reply = await Session.BuildCallReplyAsync(link, callInfo, lookup, policyLookup, _identity, ct).ConfigureAwait(false);
        try
        {
            await link.SendGatedAsync((s, c) => s.SendAsync(reply, c), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The link may already be dead -- RunLinkAsync's own pump
            // failure path notices separately and respawns.
        }
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}

/// <summary>
/// One seed's connection state inside a <see cref="StationPool"/>. Not
/// part of the public API -- app code interacts with the pool, never a
/// link directly (aside from <see cref="StationPool.PickConnectedSession"/>
/// handing back the underlying Session for dedicated-stream use).
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class PooledLink : IFrameSink
{
    public Seed Seed { get; }
    public Session? Session { get; set; }
    public bool Connected { get; set; }
    public ConcurrentDictionary<string, TaskCompletionSource<CallResponse>> PendingCalls { get; } = new();

    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public PooledLink(Seed seed)
    {
        Seed = seed;
    }

    /// <summary>
    /// The single choke point for every outbound write on this link's
    /// Session -- every caller (pool PublishAsync/SubscribeAsync/
    /// AdvertiseAsync/CallAsync, replay, an inbound CALL's reply, and the
    /// RPC-telemetry facts fired around a call) goes through this, because
    /// QuicStream.WriteAsync throws on an overlapping write and this SDK's
    /// FrameStream/Session have no serialization of their own -- correct
    /// for a Session used by one caller at a time (the documented single-
    /// Session contract), not for N concurrent pool operations sharing one
    /// physical connection.
    /// </summary>
    public async Task SendGatedAsync(Func<Session, CancellationToken, Task> send, CancellationToken ct)
    {
        var session = Session ?? throw new InvalidOperationException("link is not connected");
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await send(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task PublishAsync(PublishSpec spec, CancellationToken ct = default) =>
        SendGatedAsync((s, c) => s.PublishAsync(spec, c), ct);
}
