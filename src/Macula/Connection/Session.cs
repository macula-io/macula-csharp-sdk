using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Connection;

public sealed class ConnectRefusedException : Exception
{
    public long? RefusalCode { get; }

    public ConnectRefusedException(long? refusalCode)
        : base(refusalCode is { } code ? $"station refused the connection (refusal_code={code})" : "station refused the connection")
    {
        RefusalCode = refusalCode;
    }
}

/// <summary>
/// A live connection to one macula station: the QUIC transport, the
/// control stream, and the CONNECT/HELLO handshake state machine. Client
/// side of `macula_peering_conn.erl`'s `gen_statem`:
/// connecting -&gt; handshaking -&gt; connected -&gt; draining -&gt; terminated.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class Session : IAsyncDisposable
{
    private readonly QuicConnection _connection;
    private readonly FrameStream _control;
    private bool _closed;

    public KeyPair Identity { get; }
    public HelloInfo RemoteInfo { get; }

    private Session(QuicConnection connection, FrameStream control, KeyPair identity, HelloInfo remoteInfo)
    {
        _connection = connection;
        _control = control;
        Identity = identity;
        RemoteInfo = remoteInfo;
    }

    /// <summary>
    /// Dial <paramref name="host"/>:<paramref name="port"/>, open the
    /// control stream, send a signed CONNECT, and wait for HELLO. Throws
    /// <see cref="ConnectRefusedException"/> if the station's HELLO carries
    /// `accepted = false`, or <see cref="OperationCanceledException"/> if
    /// no HELLO arrives within <paramref name="handshakeTimeout"/> (30s
    /// default, matching `HANDSHAKE_TIMEOUT_MS` -- its most common
    /// real-world trigger is a protocol version mismatch, which looks like
    /// a plain timeout, not an explicit error frame).
    /// </summary>
    public static async Task<Session> ConnectAsync(
        string host,
        int port,
        KeyPair identity,
        Trust trust,
        TimeSpan? handshakeTimeout = null,
        CancellationToken ct = default)
    {
        var clientOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint(host, port),
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { new("macula") },
                TargetHost = host,
                RemoteCertificateValidationCallback = TrustValidation.BuildCallback(trust),
            },
        };

        var connection = await QuicConnection.ConnectAsync(clientOptions, ct).ConfigureAwait(false);
        QuicStream? controlStream = null;
        try
        {
            controlStream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
            var control = new FrameStream(controlStream);

            var puzzleEvidence = Puzzle.Evidence(identity.PublicBytes());
            var connectSpec = ConnectSpec.New(identity.PublicBytes(), puzzleEvidence);
            var signed = Envelope.Sign(ConnectFrame.Build(connectSpec), identity);
            await control.SendFrameAsync(signed, ct).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(handshakeTimeout ?? TimeSpan.FromSeconds(30));

            Value helloFrame;
            try
            {
                helloFrame = await control.RecvFrameAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "no HELLO within the handshake timeout -- the most common real-world cause is a " +
                    "protocol version mismatch, which the station's own peering layer doesn't report as " +
                    "an explicit error frame");
            }

            var helloInfo = HelloFrame.Parse(helloFrame);
            if (!helloInfo.Accepted)
            {
                throw new ConnectRefusedException(helloInfo.RefusalCode);
            }

            return new Session(connection, control, identity, helloInfo);
        }
        catch
        {
            if (controlStream is not null)
            {
                await controlStream.DisposeAsync().ConfigureAwait(false);
            }
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a frame on the control stream, auto-signing it first.</summary>
    public Task SendAsync(Value.MapValue frame, CancellationToken ct = default) =>
        _control.SendFrameAsync(Envelope.Sign(frame, Identity), ct);

    /// <summary>Receives the next frame off the control stream.</summary>
    public Task<Value> RecvAsync(CancellationToken ct = default) => _control.RecvFrameAsync(ct);

    /// <summary>Opens a fresh dedicated QUIC stream (streaming RPC session, content transfer).</summary>
    public async Task<FrameStream> OpenDedicatedStreamAsync(CancellationToken ct = default)
    {
        var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
        return new FrameStream(stream);
    }

    /// <summary>
    /// Accepts the next inbound dedicated stream the peer opens toward us
    /// (an advertised procedure's inbound STREAM_OPEN). Blocks until one
    /// arrives.
    /// </summary>
    public async Task<FrameStream> AcceptDedicatedStreamAsync(CancellationToken ct = default)
    {
        var stream = await _connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
        return new FrameStream(stream);
    }

    /// <summary>Sends GOODBYE and closes the connection. Idempotent.</summary>
    public async ValueTask CloseAsync(string reason = "normal", string? detail = null)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;

        try
        {
            await SendAsync(GoodbyeFrame.Build(reason, detail)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort -- the connection may already be unusable if
            // we're closing because of a transport-level failure.
        }

        await _connection.CloseAsync(0).ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
