using System.Security.Cryptography;
using Macula.Cbor;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Dht;

/// <summary>
/// The subset of Macula's PKARR-compatible signed DHT records that
/// direct-dial resolution needs: procedure_advertisement, station_endpoint,
/// and content_announcement construction, signing, verification, and
/// storage-key derivation. Ported from macula-io/macula's
/// src/record/macula_record.erl via macula-go's own dht/record.go (the
/// exact same algorithm, byte-for-byte -- this is not a fresh derivation).
/// </summary>
public static class RecordTypes
{
    public const byte ProcedureAdvertisement = 0x06;
    public const byte ContentAnnouncement = 0x11;
    public const byte StationEndpoint = 0x12;
}

/// <summary>Matches macula_record's ?DEFAULT_TTL_MS (48h) -- the TTL a procedure_advertisement gets when unspecified.</summary>
public static class RecordDefaults
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(48);
}

/// <summary>
/// Mirrors macula_record.erl's envelope map (type/key/version/created_at/
/// expires_at/payload/signature). subject_id is not carried -- neither
/// record type this SDK builds uses it.
/// </summary>
public sealed class Record
{
    public required byte Type { get; init; }
    public required byte[] Key { get; init; } // 32B: envelope signer's Ed25519 pubkey
    public required byte[] Version { get; init; } // 16B: UUIDv7
    public required long CreatedAt { get; init; } // ms since epoch
    public required long ExpiresAt { get; init; } // ms since epoch
    public required Value Payload { get; init; }
    public byte[]? Signature { get; init; } // 64B, set by Sign
}

public static class RecordFactory
{
    // Ed25519 signature domain separator -- macula_record's ?SIG_DOMAIN.
    // 17 bytes: "macula-v2-record" (16 ASCII) plus a trailing NUL.
    private static readonly byte[] SigDomain = "macula-v2-record\0"u8.ToArray();

    private const string StorageDomainStationEndpoint = "station_endpoint";

    /// <summary>
    /// Builds an UNSIGNED procedure_advertisement record naming
    /// servingStation as procedureUri's current handler. procedureUri
    /// should be the realm-qualified discovery URI (see DiscoveryUri) --
    /// the advertiser and the resolver must derive the identical URI or the
    /// DHT storage key (ProcedureKey) will not agree. Sign before PutRecord.
    /// Mirrors macula_record:procedure_advertisement/3,4.
    /// </summary>
    public static Record NewProcedureAdvertisement(byte[] advertiserNode, string procedureUri, byte[] servingStation, TimeSpan ttl)
    {
        if (advertiserNode.Length != 32)
        {
            throw new ArgumentException($"advertiser node must be 32 bytes, got {advertiserNode.Length}", nameof(advertiserNode));
        }
        if (servingStation.Length != 32)
        {
            throw new ArgumentException($"serving station must be 32 bytes, got {servingStation.Length}", nameof(servingStation));
        }
        if (ttl <= TimeSpan.Zero)
        {
            ttl = RecordDefaults.DefaultTtl;
        }
        var payload = Value.Map(new List<KeyValuePair<Value, Value>>
        {
            new(Value.Text("procedure_uri"), Value.Text(procedureUri)),
            new(Value.Text("advertiser_node"), Value.Bytes(advertiserNode)),
            new(Value.Text("serving_station"), Value.Bytes(servingStation)),
        });
        return NewEnvelope(RecordTypes.ProcedureAdvertisement, advertiserNode, payload, ttl);
    }

    /// <summary>
    /// NewProcedureAdvertisement plus an embedded X.509 service-cert chain
    /// (leaf-first PEM: leaf ++ org CA), for Slice 7c Direction B
    /// managed-realm authorization -- see CertChain.Verify. Opt-in: plain
    /// NewProcedureAdvertisement is unaffected.
    /// </summary>
    public static Record NewProcedureAdvertisementWithCertChain(byte[] advertiserNode, string procedureUri, byte[] servingStation, TimeSpan ttl, byte[] certChainPem)
    {
        var rec = NewProcedureAdvertisement(advertiserNode, procedureUri, servingStation, ttl);
        var payload = (Value.MapValue)rec.Payload;
        var entries = payload.Entries.ToList();
        entries.Add(new KeyValuePair<Value, Value>(Value.Text("cert_chain"), Value.Bytes(certChainPem)));
        return new Record
        {
            Type = rec.Type,
            Key = rec.Key,
            Version = rec.Version,
            CreatedAt = rec.CreatedAt,
            ExpiresAt = rec.ExpiresAt,
            Payload = Value.Map(entries),
        };
    }

    /// <summary>Builds an UNSIGNED content_announcement record naming announcerNode as reachable at endpoint for mcid. Mirrors macula_record:content_announcement/3,4.</summary>
    public static Record NewContentAnnouncement(byte[] announcerNode, byte[] mcid, string endpoint, TimeSpan ttl)
    {
        if (announcerNode.Length != 32)
        {
            throw new ArgumentException($"announcer node must be 32 bytes, got {announcerNode.Length}", nameof(announcerNode));
        }
        if (mcid.Length != 34)
        {
            throw new ArgumentException($"mcid must be 34 bytes, got {mcid.Length}", nameof(mcid));
        }
        var payload = Value.Map(new List<KeyValuePair<Value, Value>>
        {
            new(Value.Text("announcer_node"), Value.Bytes(announcerNode)),
            new(Value.Text("mcid"), Value.Bytes(mcid)),
            new(Value.Text("endpoint"), Value.Text(endpoint)),
        });
        return NewEnvelope(RecordTypes.ContentAnnouncement, announcerNode, payload, ttl);
    }

    private static Record NewEnvelope(byte type, byte[] key, Value payload, TimeSpan ttl)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new Record
        {
            Type = type,
            Key = key,
            Version = Envelope.FreshFrameId(),
            CreatedAt = now,
            ExpiresAt = now + (long)ttl.TotalMilliseconds,
            Payload = payload,
        };
    }

    /// <summary>
    /// The exact bytes macula_record:canonical_unsigned/1 signs and
    /// verifies: deterministic CBOR of the envelope map using the COMPACT
    /// single-letter keys (t/k/v/c/x/p), signature excluded. This is a
    /// DIFFERENT representation from the full-field-name map PutRecord
    /// sends as RPC args -- the compact form exists only to be
    /// signed/verified, never sent on the wire as such.
    /// </summary>
    private static byte[] CanonicalUnsigned(Record r)
    {
        var entries = new List<KeyValuePair<Value, Value>>
        {
            new(Value.Text("t"), Value.UInt(r.Type)),
            new(Value.Text("k"), Value.Bytes(r.Key)),
            new(Value.Text("v"), Value.Bytes(r.Version)),
            new(Value.Text("c"), Value.Int(r.CreatedAt)),
            new(Value.Text("x"), Value.Int(r.ExpiresAt)),
            new(Value.Text("p"), r.Payload),
        };
        return CborCodec.Encode(Value.Map(entries));
    }

    /// <summary>Returns r with Signature set to the Ed25519 signature over SigDomain || CanonicalUnsigned(r), matching macula_record:sign/2.</summary>
    public static Record Sign(Record r, KeyPair id)
    {
        var canonical = CanonicalUnsigned(r);
        var msg = new byte[SigDomain.Length + canonical.Length];
        SigDomain.CopyTo(msg, 0);
        canonical.CopyTo(msg, SigDomain.Length);
        var sig = id.Sign(msg);
        return new Record
        {
            Type = r.Type,
            Key = r.Key,
            Version = r.Version,
            CreatedAt = r.CreatedAt,
            ExpiresAt = r.ExpiresAt,
            Payload = r.Payload,
            Signature = sig,
        };
    }

    public enum VerifyError
    {
        InvalidSignature,
        Expired,
    }

    /// <summary>Checks r's Ed25519 signature against its own Key, then its expiry. Matches macula_record:verify/1. Returns null on success.</summary>
    public static VerifyError? Verify(Record r)
    {
        if (r.Signature is not { Length: 64 } || r.Key.Length != 32)
        {
            return VerifyError.InvalidSignature;
        }
        var canonical = CanonicalUnsigned(r);
        var msg = new byte[SigDomain.Length + canonical.Length];
        SigDomain.CopyTo(msg, 0);
        canonical.CopyTo(msg, SigDomain.Length);
        if (!KeyPair.Verify(msg, r.Signature, r.Key))
        {
            return VerifyError.InvalidSignature;
        }
        if (r.ExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= r.ExpiresAt)
        {
            return VerifyError.Expired;
        }
        return null;
    }

    /// <summary>The DHT storage key for a procedure_advertisement by its (already realm-qualified) URI: SHA-256(uri). Matches macula_record:procedure_key/1.</summary>
    public static byte[] ProcedureKey(string procedureUri) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(procedureUri));

    /// <summary>The DHT storage key for a station's own station_endpoint record: SHA-256("station_endpoint" || pubkey). Matches macula_record:station_endpoint_key/1.</summary>
    public static byte[] StationEndpointKey(byte[] stationPubkey)
    {
        var domain = System.Text.Encoding.ASCII.GetBytes(StorageDomainStationEndpoint);
        var buf = new byte[domain.Length + stationPubkey.Length];
        domain.CopyTo(buf, 0);
        stationPubkey.CopyTo(buf, domain.Length);
        return SHA256.HashData(buf);
    }

    /// <summary>The DHT storage key for every content_announcement naming mcid: SHA-256(mcid). Matches macula_record:content_key/1.</summary>
    public static byte[] ContentKey(byte[] mcid) => SHA256.HashData(mcid);

    /// <summary>
    /// Matches macula_direct_dial's discovery_uri/2: the DHT lookup/
    /// advertisement key input is hex(realm) + "/" + procedure, so the same
    /// procedure name under different realms doesn't collide in the DHT.
    /// </summary>
    public static string DiscoveryUri(byte[] realm, string procedure) => Convert.ToHexStringLower(realm) + "/" + procedure;
}

/// <summary>procedure_advertisement fields, read out of a Record's payload. CertChain is null when the advertisement carries none (the common, unmanaged-realm case).</summary>
public sealed record ProcedureAdvertisement(string ProcedureUri, byte[] AdvertiserNode, byte[] ServingStation, byte[]? CertChain);

/// <summary>station_endpoint fields, read out of a Record's payload.</summary>
public sealed record StationEndpoint(ushort QuicPort, IReadOnlyList<string> HostAdvertised);

/// <summary>content_announcement fields, read out of a Record's payload.</summary>
public sealed record ContentAnnouncement(byte[] AnnouncerNode, byte[] Mcid, string Endpoint);

public static class RecordReading
{
    public static ProcedureAdvertisement ReadProcedureAdvertisement(Record r)
    {
        if (r.Type != RecordTypes.ProcedureAdvertisement)
        {
            throw new InvalidOperationException($"not a procedure_advertisement record (type={r.Type})");
        }
        var payload = (Value.MapValue)r.Payload;
        var uri = RequireText(payload, "procedure_uri");
        var adv = RequireBytes(payload, "advertiser_node");
        var station = RequireBytes(payload, "serving_station");
        if (adv.Length != 32 || station.Length != 32)
        {
            throw new InvalidOperationException("malformed procedure_advertisement payload");
        }
        byte[]? certChain = payload.Get("cert_chain") is { } cc ? cc.AsBytes() : null;
        return new ProcedureAdvertisement(uri, adv, station, certChain);
    }

    public static StationEndpoint ReadStationEndpoint(Record r)
    {
        if (r.Type != RecordTypes.StationEndpoint)
        {
            throw new InvalidOperationException($"not a station_endpoint record (type={r.Type})");
        }
        var payload = (Value.MapValue)r.Payload;
        var portV = payload.Get("quic_port") ?? throw new InvalidOperationException("station_endpoint missing quic_port");
        var portI = portV.AsInt();
        if (portI is <= 0 or > 65535)
        {
            throw new InvalidOperationException("station_endpoint has a malformed quic_port");
        }
        var hosts = new List<string>();
        if (payload.Get("host_advertised") is Value.ListValue list)
        {
            foreach (var item in list.Items)
            {
                // Each host is a bare Erlang binary on the wire (CBOR byte
                // string, major type 2), not text -- confirmed against a
                // real station's own published record. Value.AsText()
                // already falls back to decoding bytes as UTF-8, so this
                // just works without special-casing (unlike the Go SDK,
                // which needed an explicit bytes-first fallback).
                hosts.Add(item.AsText());
            }
        }
        return new StationEndpoint((ushort)portI, hosts);
    }

    public static ContentAnnouncement ReadContentAnnouncement(Record r)
    {
        if (r.Type != RecordTypes.ContentAnnouncement)
        {
            throw new InvalidOperationException($"not a content_announcement record (type={r.Type})");
        }
        var payload = (Value.MapValue)r.Payload;
        var announcer = RequireBytes(payload, "announcer_node");
        var mcid = RequireBytes(payload, "mcid");
        var endpoint = RequireText(payload, "endpoint");
        if (announcer.Length != 32 || mcid.Length != 34)
        {
            throw new InvalidOperationException("malformed content_announcement payload");
        }
        return new ContentAnnouncement(announcer, mcid, endpoint);
    }

    private static byte[] RequireBytes(Value.MapValue m, string field) =>
        m.Get(field)?.AsBytes() ?? throw new InvalidOperationException($"missing field {field}");

    private static string RequireText(Value.MapValue m, string field) =>
        m.Get(field)?.AsText() ?? throw new InvalidOperationException($"missing field {field}");
}
