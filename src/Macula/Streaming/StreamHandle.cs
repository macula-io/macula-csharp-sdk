using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Streaming;

/// <summary>
/// General-purpose streaming RPC, both caller/consumer and provider roles,
/// ported from `macula_stream_sink.erl`. Like content transfer, this is
/// not a separate wire mechanism: it runs the STREAM_* frame types over a
/// dedicated QUIC stream rather than the control stream.
///
/// Both roles end up holding the same <see cref="StreamHandle"/> --
/// a stream's wire vocabulary (STREAM_DATA/END/ERROR/REPLY) is symmetric
/// regardless of which side opened it, so SendData/Recv/CloseSend/Abort
/// all mean the same thing either way. <see cref="SendReplyAsync"/> is the
/// one provider-only addition.
///
/// Caller/consumer usage:
/// 1. <see cref="OpenAsync"/> sends STREAM_OPEN and returns a handle once
///    the frame is on the wire -- no open-time acknowledgement to wait for.
/// 2. Drive a receive loop with <see cref="RecvAsync"/> until
///    <see cref="StreamItem.Eof"/> or an error.
/// 3. For client_stream/bidi modes wanting a result: <see cref="SendDataAsync"/>
///    each chunk in order, <see cref="CloseSendAsync"/> when done, then
///    <see cref="AwaitReplyAsync"/>.
/// 4. Non-normal termination must call <see cref="AbortAsync"/>, not just
///    drop the handle -- the peer's only signal to tell a
///    cancellation/failure apart from a dropped connection.
///
/// Provider usage:
/// 1. <see cref="Session.AdvertiseAsync"/> once per procedure this session
///    will answer.
/// 2. Loop on <see cref="AcceptAsync"/>, which blocks for the next inbound
///    STREAM_OPEN and hands back a ready-to-use handle plus the parsed
///    <see cref="StreamOpenInfo"/>.
/// 3. Drive it exactly like the caller side, from the opposite chair.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class StreamHandle
{
    private readonly FrameStream _stream;
    private ulong _seqOut;

    public byte[] StreamId { get; }
    public StreamMode Mode { get; }

    private StreamHandle(FrameStream stream, byte[] streamId, StreamMode mode)
    {
        _stream = stream;
        StreamId = streamId;
        Mode = mode;
    }

    /// <summary>
    /// Open a dedicated stream on <paramref name="session"/>'s connection
    /// and send a signed STREAM_OPEN. Fire-and-forget at the wire level --
    /// drive <see cref="RecvAsync"/> (for server_stream/bidi) or
    /// <see cref="SendDataAsync"/> (for client_stream/bidi) next.
    /// </summary>
    public static async Task<StreamHandle> OpenAsync(Session session, string procedure, byte[] realm, StreamMode mode, Value args, long deadlineMs, KeyPair identity, CancellationToken ct = default)
    {
        var stream = await session.OpenDedicatedStreamAsync(ct).ConfigureAwait(false);
        var streamId = new byte[16];
        Random.Shared.NextBytes(streamId);
        var spec = new StreamOpenSpec
        {
            StreamId = streamId,
            Procedure = procedure,
            Realm = realm,
            Mode = mode,
            Args = args,
            DeadlineMs = deadlineMs,
            Caller = identity.NodeId(),
        };
        var signed = Envelope.Sign(StreamOpenFrame.Build(spec), identity);
        await stream.SendFrameAsync(signed, ct).ConfigureAwait(false);
        return new StreamHandle(stream, streamId, mode);
    }

    /// <summary>
    /// Provider role: block for the next inbound STREAM_OPEN on
    /// <paramref name="session"/>'s connection, bounded by
    /// <paramref name="timeout"/>. Only ever succeeds after
    /// <see cref="Session.AdvertiseAsync"/> has registered at least one
    /// procedure. Returns the ready-to-use handle alongside the parsed
    /// <see cref="StreamOpenInfo"/> (check its Procedure if this session
    /// advertised more than one).
    /// </summary>
    public static async Task<(StreamHandle Handle, StreamOpenInfo Info)> AcceptAsync(Session session, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        FrameStream stream;
        try
        {
            stream = await session.AcceptDedicatedStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("no inbound stream within the given timeout");
        }

        var first = await stream.RecvFrameAsync(ct).ConfigureAwait(false);
        var open = StreamOpenFrameParsing.Parse(first);
        var handle = new StreamHandle(stream, open.StreamId, open.Mode);
        return (handle, open);
    }

    /// <summary>
    /// Provider role: send the terminal STREAM_REPLY a client_stream/bidi
    /// caller's own <see cref="AwaitReplyAsync"/> is waiting on, once this
    /// side has fully consumed and verified whatever the caller streamed.
    /// </summary>
    public Task SendReplyAsync(Value payload, KeyPair identity, CancellationToken ct = default)
    {
        var spec = new StreamReplySpec { StreamId = StreamId, Payload = payload, RespondedBy = identity.NodeId() };
        return _stream.SendFrameAsync(Envelope.Sign(StreamReplyFrame.Build(spec), identity), ct);
    }

    /// <summary>
    /// Send one chunk. Seq is tracked internally, starting at 0 and
    /// incrementing per call -- a sanity/debugging signal, not used for
    /// reordering (frames arrive in order on a single QUIC stream by
    /// construction).
    /// </summary>
    public Task SendDataAsync(StreamEncoding encoding, Value body, KeyPair identity, CancellationToken ct = default)
    {
        var spec = new StreamDataSpec { StreamId = StreamId, Seq = _seqOut, Encoding = encoding, Body = body };
        _seqOut++;
        return _stream.SendFrameAsync(Envelope.Sign(StreamDataFrame.Build(spec), identity), ct);
    }

    /// <summary>Half-close: signal this side is done sending. For client_stream/bidi modes, follow with <see cref="AwaitReplyAsync"/>.</summary>
    public Task CloseSendAsync(KeyPair identity, CancellationToken ct = default)
    {
        var spec = new StreamEndSpec { StreamId = StreamId, Role = StreamRole.Send };
        return _stream.SendFrameAsync(Envelope.Sign(StreamEndFrame.Build(spec), identity), ct);
    }

    public enum RecvErrorKind { PeerAborted, StreamIdMismatch, UnexpectedFrame }

    public sealed class RecvStreamException : Exception
    {
        public RecvErrorKind Kind { get; }
        public string? Code { get; }

        public RecvStreamException(RecvErrorKind kind, string message, string? code = null) : base(message)
        {
            Kind = kind;
            Code = code;
        }
    }

    /// <summary>Receive the next chunk or end-of-stream, bounded by <paramref name="timeout"/>.</summary>
    public async Task<StreamItem> RecvAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var value = await RecvFrameTimeoutAsync(timeout, ct).ConfigureAwait(false);
        var evt = StreamFrameParsing.ParseStreamEvent(value);
        switch (evt)
        {
            case StreamEvent.Data d:
                CheckStreamId(d.StreamId);
                return new StreamItem.Data(d.Seq, d.Encoding, d.Body);
            case StreamEvent.End e:
                CheckStreamId(e.StreamId);
                return StreamItem.Eof;
            case StreamEvent.Error err:
                CheckStreamId(err.StreamId);
                throw new RecvStreamException(RecvErrorKind.PeerAborted, $"peer aborted the stream: {err.Code} ({err.Message})", err.Code);
            case StreamEvent.Reply:
                throw new RecvStreamException(RecvErrorKind.UnexpectedFrame, "received a frame not valid in this context");
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Block for the provider's terminal STREAM_REPLY (client_stream/bidi modes only) -- call after <see cref="CloseSendAsync"/>.</summary>
    public async Task<(Value Payload, byte[] RespondedBy)> AwaitReplyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var value = await RecvFrameTimeoutAsync(timeout, ct).ConfigureAwait(false);
        var evt = StreamFrameParsing.ParseStreamEvent(value);
        switch (evt)
        {
            case StreamEvent.Reply r:
                CheckStreamId(r.StreamId);
                return (r.Payload, r.RespondedBy);
            case StreamEvent.Error err:
                CheckStreamId(err.StreamId);
                throw new RecvStreamException(RecvErrorKind.PeerAborted, $"peer aborted the stream: {err.Code} ({err.Message})", err.Code);
            case StreamEvent.Data or StreamEvent.End:
                throw new RecvStreamException(RecvErrorKind.UnexpectedFrame, "received a frame not valid in this context");
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    private async Task<Value> RecvFrameTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await _stream.RecvFrameAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no frame within {timeout}");
        }
    }

    private void CheckStreamId(byte[] streamId)
    {
        if (!streamId.AsSpan().SequenceEqual(StreamId))
        {
            throw new RecvStreamException(RecvErrorKind.StreamIdMismatch, "received a frame for a different stream_id");
        }
    }

    /// <summary>
    /// Non-normal termination: explicitly tell the peer this stream is
    /// aborting -- the only signal the peer gets to distinguish a
    /// cancellation/failure from a dropped connection. Best-effort.
    /// </summary>
    public async Task AbortAsync(string code, string message, KeyPair identity, CancellationToken ct = default)
    {
        var spec = new StreamErrorSpec { StreamId = StreamId, Code = code, Message = message };
        try
        {
            await _stream.SendFrameAsync(Envelope.Sign(StreamErrorFrame.Build(spec), identity), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort, matching Session.CloseAsync's own GOODBYE.
        }
    }
}

/// <summary>One item <see cref="StreamHandle.RecvAsync"/> hands back: a chunk, or a clean end-of-stream.</summary>
public abstract record StreamItem
{
    private StreamItem() { }

    public sealed record Data(ulong Seq, StreamEncoding Encoding, Value Body) : StreamItem;

    private sealed record EofItem : StreamItem;

    public static readonly StreamItem Eof = new EofItem();
}
