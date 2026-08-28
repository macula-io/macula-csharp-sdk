using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Macula.Connection;

/// <summary>
/// TLS-layer verification mode for a CONNECT dial, matching
/// `macula_peering_conn.erl`'s `dial_trust_opts/1`. Independent of the
/// frame-level identity check every CONNECT/HELLO undergoes regardless
/// (the signed frame's own `node_id` is always verified) -- this only
/// controls whether the QUIC/TLS handshake itself trusts the peer's
/// certificate.
/// </summary>
public abstract record Trust
{
    private Trust() { }

    /// <summary>
    /// Standard CA-bundle + hostname validation. Used for bootstrap-style
    /// dials by hostname where the peer's Ed25519 identity isn't known yet
    /// -- the default trust mode since macula 5.0.0, and what the public
    /// demo fleet (station-de-frankfurt.macula.io) actually presents (a
    /// 3-certificate RSA chain via Let's Encrypt, not a self-signed
    /// Ed25519 leaf cert).
    /// </summary>
    public sealed record WebPki : Trust;

    /// <summary>
    /// Pins the server cert's public key to this exact 32-byte Ed25519
    /// NodeId, bypassing chain/hostname validation entirely. Used when the
    /// dialer already knows the peer's identity (DHT records, pre-shared
    /// relay identities).
    /// </summary>
    public sealed record Pinned(byte[] NodeId) : Trust;

    /// <summary>
    /// Skips TLS verification entirely. Dev/lab only -- the application-layer
    /// signed-frame identity check still applies regardless, but the QUIC/TLS
    /// session itself is unauthenticated. Never use this against a real
    /// deployment; a machine-in-the-middle can freely intercept traffic
    /// before the frame-level check ever runs.
    /// </summary>
    public sealed record Insecure : Trust;

    public static readonly Trust UseWebPki = new WebPki();
    public static Trust Pin(byte[] nodeId) => new Pinned(nodeId);
    public static readonly Trust Unsafe = new Insecure();
}

internal static class TrustValidation
{
    /// <summary>
    /// Builds the certificate-validation callback for
    /// <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/>,
    /// or null for <see cref="Trust.WebPki"/> to fall through to .NET's own
    /// default CA-bundle + hostname validation.
    /// </summary>
    public static RemoteCertificateValidationCallback? BuildCallback(Trust trust) => trust switch
    {
        Trust.WebPki => null,
        Trust.Insecure => (_, _, _, _) => true,
        Trust.Pinned pinned => (_, certificate, _, _) => MatchesPinnedKey(certificate, pinned.NodeId),
        _ => throw new ArgumentOutOfRangeException(nameof(trust)),
    };

    /// <summary>
    /// Extracts the leaf certificate's raw Ed25519 public key and compares
    /// it to the pinned NodeId. Per RFC 8410, an Ed25519 certificate's
    /// SubjectPublicKeyInfo BIT STRING contains the 32-byte public key
    /// directly with no further ASN.1 nesting, so the legacy
    /// `GetPublicKey()` accessor (which returns that BIT STRING's raw
    /// content) already yields exactly the bytes needed -- no ASN.1
    /// parsing of our own required.
    /// </summary>
    private static bool MatchesPinnedKey(X509Certificate? certificate, byte[] nodeId)
    {
        if (certificate is not X509Certificate2 cert)
        {
            return false;
        }

        byte[] rawKey;
        try
        {
            rawKey = cert.GetPublicKey();
        }
        catch (Exception)
        {
            return false;
        }

        return rawKey.Length == 32 && rawKey.AsSpan().SequenceEqual(nodeId);
    }
}
