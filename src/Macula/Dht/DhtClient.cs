using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;

namespace Macula.Dht;

/// <summary>
/// Thin RPC wrappers over the mesh's `_dht.*` procedures -- NOT a
/// reimplementation of Kademlia/DHT peer logic, which stays relay-only
/// (macula-station). A leaf identity just asks whatever station it's
/// already connected to look something up in, or write something to, the
/// DHT the stations maintain among themselves. Mirrors macula-go-sdk's
/// dht/client.go exactly.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public static class DhtClient
{
    // All-zero 32-byte realm DHT traffic travels under -- protocol-internal
    // infrastructure, matches macula.erl's ?DHT_REALM.
    private static readonly byte[] DhtRealm = new byte[32];

    // Matches macula.erl's ?DHT_RECORD_TIMEOUT_MS.
    private static readonly TimeSpan DhtTimeout = TimeSpan.FromSeconds(5);

    private const string PutRecordProc = "_dht.put_record";
    private const string FindRecordProc = "_dht.find_record";
    private const string FindRecordsProc = "_dht.find_records";
    private const string FindRecordsByTypeProc = "_dht.find_records_by_type";

    public sealed class DhtException : Exception
    {
        public DhtException(string message) : base(message) { }
    }

    public sealed class NotFoundException : Exception
    {
        public NotFoundException() : base("dht: record not found") { }
    }

    // The FULL-field-name map macula.erl's put_record/2 sends as a CALL's
    // args (and find_record/find_records return as a RESULT) -- distinct
    // from the compact single-letter envelope RecordFactory signs/verifies,
    // which never goes on the wire as such.
    private static Value ToRpcValue(Record r)
    {
        var entries = new List<KeyValuePair<Value, Value>>
        {
            new(Value.Text("type"), Value.UInt(r.Type)),
            new(Value.Text("key"), Value.Bytes(r.Key)),
            new(Value.Text("version"), Value.Bytes(r.Version)),
            new(Value.Text("created_at"), Value.Int(r.CreatedAt)),
            new(Value.Text("expires_at"), Value.Int(r.ExpiresAt)),
            new(Value.Text("payload"), r.Payload),
        };
        if (r.Signature is { Length: 64 } sig)
        {
            entries.Add(new KeyValuePair<Value, Value>(Value.Text("signature"), Value.Bytes(sig)));
        }
        return Value.Map(entries);
    }

    private static Record RecordFromRpcValue(Value v)
    {
        if (v is not Value.MapValue m)
        {
            throw new DhtException("dht: record reply is not a map");
        }
        var type = (byte)(m.Get("type")?.AsInt() ?? throw new DhtException("dht: record reply missing type"));
        var key = m.Get("key")?.AsBytes() ?? throw new DhtException("dht: record reply missing key");
        var version = m.Get("version")?.AsBytes() ?? throw new DhtException("dht: record reply missing version");
        var createdAt = m.Get("created_at")?.AsInt() ?? throw new DhtException("dht: record reply missing created_at");
        var expiresAt = m.Get("expires_at")?.AsInt() ?? throw new DhtException("dht: record reply missing expires_at");
        var payload = m.Get("payload") ?? throw new DhtException("dht: record reply missing payload");
        var signature = m.Get("signature") is { } sigV && !sigV.IsNull ? sigV.AsBytes() : null;
        return new Record { Type = type, Key = key, Version = version, CreatedAt = createdAt, ExpiresAt = expiresAt, Payload = payload, Signature = signature };
    }

    /// <summary>Stores a signed record in the mesh DHT. Mirrors macula:put_record/2 -- the relay validates the signature on receipt.</summary>
    public static async Task PutRecordAsync(Session session, Record rec, CancellationToken ct = default)
    {
        var resp = await session.CallAsync(PutRecordProc, DhtRealm, ToRpcValue(rec), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)DhtTimeout.TotalMilliseconds, DhtTimeout, ct).ConfigureAwait(false);
        if (resp is CallResponse.Error e)
        {
            throw new DhtException($"dht: put_record failed: {e.Name}");
        }
    }

    /// <summary>Fetches one record by its storage key. Throws NotFoundException if none exists -- the caller's signature should still be checked via RecordFactory.Verify before the payload is trusted.</summary>
    public static async Task<Record> FindRecordAsync(Session session, byte[] key, CancellationToken ct = default)
    {
        var args = Value.Map(new List<KeyValuePair<Value, Value>> { new(Value.Text("key"), Value.Bytes(key)) });
        var resp = await session.CallAsync(FindRecordProc, DhtRealm, args, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)DhtTimeout.TotalMilliseconds, DhtTimeout, ct).ConfigureAwait(false);
        if (resp is CallResponse.Error e)
        {
            throw new DhtException($"dht: find_record failed: {e.Name}");
        }
        var payload = ((CallResponse.Result)resp).Payload;
        if (payload is Value.TextValue or Value.BytesValue && payload.AsText() == "not_found")
        {
            throw new NotFoundException();
        }
        return RecordFromRpcValue(payload);
    }

    /// <summary>Fetches every record stored at key -- the full signer-deduped multiset. Each record's signature should be verified via RecordFactory.Verify before its payload is trusted.</summary>
    public static async Task<IReadOnlyList<Record>> FindRecordsAsync(Session session, byte[] key, CancellationToken ct = default)
    {
        var args = Value.Map(new List<KeyValuePair<Value, Value>> { new(Value.Text("key"), Value.Bytes(key)) });
        var resp = await session.CallAsync(FindRecordsProc, DhtRealm, args, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)DhtTimeout.TotalMilliseconds, DhtTimeout, ct).ConfigureAwait(false);
        if (resp is CallResponse.Error e)
        {
            throw new DhtException($"dht: find_records failed: {e.Name}");
        }
        var payload = ((CallResponse.Result)resp).Payload;
        if (payload is not Value.ListValue list)
        {
            throw new DhtException("dht: find_records: expected a list reply");
        }
        var outList = new List<Record>();
        foreach (var item in list.Items)
        {
            try
            {
                outList.Add(RecordFromRpcValue(item));
            }
            catch (Exception)
            {
                // skip a malformed entry rather than fail the whole batch
            }
        }
        return outList;
    }
}
