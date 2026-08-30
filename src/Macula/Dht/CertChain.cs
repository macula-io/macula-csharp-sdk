using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Macula.Dht;

/// <summary>
/// Direct-dial dual-trust (Slice 7c Direction B) -- X.509 cert chain.
///
/// Managed realms root trust in the realm CA, not in the (keyless) realm
/// tag. A provider embeds its own service-cert chain (leaf ++ org CA, PEM)
/// in its procedure_advertisement; a verifying consumer chains it to the
/// realm CA it received at its own issuance. No publisher records, no live
/// authority -- the trust material already travels with the advertisement.
///
/// Ported from macula_record.erl's verify_advertisement_cert_chain/3 via
/// macula-go's own dht/cert_chain.go -- same algorithm, same five
/// failure modes, using X509Chain's native path validation instead of
/// hand-rolling ASN.1 walking. Opt-in: this has no effect on plain
/// (non-cert-chain) direct-dial, which remains exactly as it was.
/// </summary>
public static class CertChain
{
    public abstract class CertChainException : Exception
    {
        protected CertChainException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>The advertisement carries no cert_chain field at all -- the common, unmanaged-realm case. Not itself a sign of tampering; callers requiring managed-realm authorization should treat this as "not authorized," not evidence of an attack.</summary>
    public sealed class CertChainAbsentException : CertChainException
    {
        public CertChainAbsentException() : base("dht: advertisement carries no cert_chain") { }
    }

    /// <summary>The advertisement's own Ed25519 envelope signature does not verify -- checked BEFORE the cert chain is even examined.</summary>
    public sealed class CertChainBadSignatureException : CertChainException
    {
        public CertChainBadSignatureException(Exception inner) : base("dht: advertisement signature does not verify", inner) { }
    }

    /// <summary>cert_chain is present but is not a decodable PEM bundle containing at least one certificate.</summary>
    public sealed class CertChainUndecodableException : CertChainException
    {
        public CertChainUndecodableException(Exception? inner = null) : base("dht: cert_chain is not a decodable PEM certificate bundle", inner) { }
    }

    /// <summary>The leaf certificate's Ed25519 subject public key does not match the advertisement's own signing key -- the chain does not actually belong to whoever signed this record.</summary>
    public sealed class CertChainKeyMismatchException : CertChainException
    {
        public CertChainKeyMismatchException() : base("dht: leaf cert public key does not match the advertisement's signer") { }
    }

    /// <summary>The chain does not validate to the given realm CA (expired, wrong issuer, broken path, etc.).</summary>
    public sealed class CertChainUntrustedException : CertChainException
    {
        public CertChainUntrustedException(string detail) : base($"dht: cert chain does not validate to the trusted realm CA: {detail}") { }
    }

    /// <summary>The chain validates, but the leaf certificate's Organization (O) does not match the procedure's expected org segment -- a validly-signed cert for the WRONG org, i.e. a squat.</summary>
    public sealed class CertChainOrgMismatchException : CertChainException
    {
        public CertChainOrgMismatchException() : base("dht: leaf cert organization does not match the expected org") { }
    }

    /// <summary>
    /// Verifies a resolved procedure_advertisement record's embedded X.509
    /// service-cert chain against a trusted realm CA, for Slice 7c
    /// Direction B managed-realm authorization.
    ///
    /// realmCaPem is the realm CA the caller already trusts (obtained at
    /// its own issuance, out of band -- never resolved from the mesh
    /// itself). rec is a resolved procedure_advertisement. expectedOrg is
    /// the &lt;org&gt; segment of the procedure URI the caller intended to
    /// reach.
    ///
    /// Passes (returns normally) only when: rec's own envelope signature
    /// verifies; rec carries a cert_chain; the chain decodes to at least
    /// one certificate; the leaf certificate's Ed25519 subject public key
    /// equals rec's signing key (Key field); the leaf chains to
    /// realmCaPem; and the leaf's Organization RDN equals expectedOrg. Any
    /// other outcome throws a distinct exception type -- never silently
    /// treat an unauthorized advertisement as trusted.
    /// </summary>
    public static void VerifyAdvertisementCertChain(byte[] realmCaPem, Record rec, string expectedOrg)
    {
        if (RecordFactory.Verify(rec) is { } verr)
        {
            throw new CertChainBadSignatureException(new InvalidOperationException(verr.ToString()));
        }
        var adv = RecordReading.ReadProcedureAdvertisement(rec);
        if (adv.CertChain is not { Length: > 0 })
        {
            throw new CertChainAbsentException();
        }

        var chain = DecodeCertChain(adv.CertChain);
        var leaf = chain[0];

        var leafKey = Ed25519PublicKeyFromCert(leaf);
        if (leafKey is null || rec.Key.Length != 32 || !leafKey.AsSpan().SequenceEqual(rec.Key))
        {
            throw new CertChainKeyMismatchException();
        }

        ValidateCertPath(realmCaPem, chain);

        var org = OrganizationOf(leaf);
        if (org is null || org != expectedOrg)
        {
            throw new CertChainOrgMismatchException();
        }
    }

    // Parses a leaf-first PEM bundle (as embedded: leaf ++ org CA ++ ...)
    // into parsed certificates, leaf-first, matching
    // macula_record's pem_cert_ders/1.
    private static List<X509Certificate2> DecodeCertChain(byte[] certChainPem)
    {
        List<X509Certificate2> certs;
        try
        {
            certs = ParsePemBundle(certChainPem);
        }
        catch (Exception e) when (e is not CertChainException)
        {
            throw new CertChainUndecodableException(e);
        }
        if (certs.Count == 0)
        {
            throw new CertChainUndecodableException();
        }
        return certs;
    }

    private static List<X509Certificate2> ParsePemBundle(byte[] pemBytes)
    {
        var text = System.Text.Encoding.ASCII.GetString(pemBytes);
        var certs = new List<X509Certificate2>();
        const string beginMarker = "-----BEGIN CERTIFICATE-----";
        const string endMarker = "-----END CERTIFICATE-----";
        var pos = 0;
        while (true)
        {
            var start = text.IndexOf(beginMarker, pos, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }
            var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }
            end += endMarker.Length;
            var block = text[start..end];
            certs.Add(X509Certificate2.CreateFromPem(block));
            pos = end;
        }
        return certs;
    }

    // Per RFC 8410, an Ed25519 certificate's SubjectPublicKeyInfo BIT
    // STRING contains the 32-byte public key directly with no further
    // ASN.1 nesting -- matches TrustValidation.MatchesPinnedKey's own
    // technique in this same repo.
    private static byte[]? Ed25519PublicKeyFromCert(X509Certificate2 cert)
    {
        try
        {
            var raw = cert.GetPublicKey();
            return raw.Length == 32 ? raw : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? OrganizationOf(X509Certificate2 cert)
    {
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            // Organization (O) = OID 2.5.4.10.
            if (rdn.GetSingleElementType().Value == "2.5.4.10")
            {
                return rdn.GetSingleElementValue();
            }
        }
        return null;
    }

    // Validates chain (leaf-first: [leaf, org CA, ...]) to realmCaPem as
    // trust anchor. Any certificate past the leaf is treated as an
    // intermediate -- mirrors macula_record's validate_path/2.
    private static void ValidateCertPath(byte[] realmCaPem, List<X509Certificate2> chain)
    {
        List<X509Certificate2> roots;
        try
        {
            roots = ParsePemBundle(realmCaPem);
        }
        catch (Exception e)
        {
            throw new CertChainUntrustedException($"realm CA PEM contains no parseable certificate: {e.Message}");
        }
        if (roots.Count == 0)
        {
            throw new CertChainUntrustedException("realm CA PEM contains no parseable certificate");
        }

        using var chainBuilder = new X509Chain();
        chainBuilder.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var root in roots)
        {
            chainBuilder.ChainPolicy.CustomTrustStore.Add(root);
        }
        foreach (var intermediate in chain.Skip(1))
        {
            chainBuilder.ChainPolicy.ExtraStore.Add(intermediate);
        }
        chainBuilder.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chainBuilder.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        if (!chainBuilder.Build(chain[0]))
        {
            var status = string.Join(", ", chainBuilder.ChainStatus.Select(s => s.StatusInformation.Trim()));
            throw new CertChainUntrustedException(status.Length > 0 ? status : "chain build failed");
        }
    }
}
