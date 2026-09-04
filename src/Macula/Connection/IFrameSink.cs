using Macula.Frame;

namespace Macula.Connection;

/// <summary>
/// Anything that can send a signed PUBLISH on behalf of an identity --
/// <see cref="Session"/> itself, or (see <see cref="StationPool"/>) a
/// pool-owned adapter that routes the write through a per-link send gate
/// instead of writing to the underlying stream directly. Exists so
/// <see cref="RpcFacts"/>' telemetry auto-facts (rpc.sent_v1/completed_v1/
/// received_v1/replied_v1) go through whatever serialization the caller's
/// send path requires, rather than always reaching into a raw
/// <see cref="Session"/> and risking a concurrent write to the same QUIC
/// stream from two different callers.
/// </summary>
public interface IFrameSink
{
    Task PublishAsync(PublishSpec spec, CancellationToken ct = default);
}
