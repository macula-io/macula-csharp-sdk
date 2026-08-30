using Macula.Dht;
using Macula.Identity;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Macula.Tests;

/// <summary>
/// Mirrors macula-go's own dht/cert_chain_test.go fixtures and cases
/// exactly (valid, absent, bad signature, key mismatch, org mismatch,
/// expired, wrong CA, undecodable) -- same algorithm, same five failure
/// modes, ported test-for-test.
/// </summary>
public class CertChainTests
{
    private static (byte[] Pem, X509Certificate Cert, AsymmetricKeyParameter Priv) TestCa()
    {
        var kpGen = new Ed25519KeyPairGenerator();
        kpGen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var kp = kpGen.GenerateKeyPair();

        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.One);
        var subject = new Org.BouncyCastle.Asn1.X509.X509Name("CN=Test Realm CA, O=Test Realm CA");
        gen.SetIssuerDN(subject);
        gen.SetSubjectDN(subject);
        gen.SetNotBefore(DateTime.UtcNow.AddHours(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddHours(24));
        gen.SetPublicKey(kp.Public);
        gen.AddExtension(Org.BouncyCastle.Asn1.X509.X509Extensions.BasicConstraints, true, new Org.BouncyCastle.Asn1.X509.BasicConstraints(true));

        var signatureFactory = new Asn1SignatureFactory("Ed25519", kp.Private);
        var cert = gen.Generate(signatureFactory);
        var pem = System.Text.Encoding.ASCII.GetBytes(
            "-----BEGIN CERTIFICATE-----\n" +
            Convert.ToBase64String(cert.GetEncoded(), Base64FormattingOptions.InsertLineBreaks) +
            "\n-----END CERTIFICATE-----\n");
        return (pem, cert, kp.Private);
    }

    private static byte[] TestLeaf(X509Certificate ca, AsymmetricKeyParameter caPriv, byte[] advertiserPub, string org, DateTime notAfter)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.Two);
        gen.SetIssuerDN(ca.SubjectDN);
        gen.SetSubjectDN(new Org.BouncyCastle.Asn1.X509.X509Name($"CN=test-service, O={org}"));
        gen.SetNotBefore(DateTime.UtcNow.AddHours(-1));
        gen.SetNotAfter(notAfter);
        var pub = new Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(advertiserPub, 0);
        gen.SetPublicKey(pub);
        var signatureFactory = new Asn1SignatureFactory("Ed25519", caPriv);
        var cert = gen.Generate(signatureFactory);
        return cert.GetEncoded();
    }

    private static byte[] PemBundle(params byte[][] ders)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var der in ders)
        {
            sb.Append("-----BEGIN CERTIFICATE-----\n");
            sb.Append(Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks));
            sb.Append("\n-----END CERTIFICATE-----\n");
        }
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private const string Uri = "0000/acme-corp/widget.build_v1";

    [Fact]
    public void Valid_chain_verifies()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"); // does not throw
    }

    [Fact]
    public void Absent_chain_throws()
    {
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var rec = RecordFactory.NewProcedureAdvertisement(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1));
        rec = RecordFactory.Sign(rec, advertiser);

        var (caPem, _, _) = TestCa();
        Assert.Throws<CertChain.CertChainAbsentException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"));
    }

    [Fact]
    public void Bad_envelope_signature_throws()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);
        var tamperedSig = (byte[])rec.Signature!.Clone();
        tamperedSig[0] ^= 0xFF;
        var tampered = new Macula.Dht.Record { Type = rec.Type, Key = rec.Key, Version = rec.Version, CreatedAt = rec.CreatedAt, ExpiresAt = rec.ExpiresAt, Payload = rec.Payload, Signature = tamperedSig };

        Assert.Throws<CertChain.CertChainBadSignatureException>(() => CertChain.VerifyAdvertisementCertChain(caPem, tampered, "acme-corp"));
    }

    [Fact]
    public void Leaf_key_mismatch_throws()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var otherKey = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, otherKey.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainKeyMismatchException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"));
    }

    [Fact]
    public void Org_mismatch_throws()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), "0000/other-org/widget.build_v1", station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainOrgMismatchException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "other-org"));
    }

    [Fact]
    public void Expired_leaf_throws_untrusted()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(-1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainUntrustedException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"));
    }

    [Fact]
    public void Wrong_ca_throws_untrusted()
    {
        var (_, caCert, caPriv) = TestCa();
        var (otherCaPem, _, _) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainUntrustedException>(() => CertChain.VerifyAdvertisementCertChain(otherCaPem, rec, "acme-corp"));
    }

    [Fact]
    public void Undecodable_chain_throws()
    {
        var (caPem, _, _) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), System.Text.Encoding.ASCII.GetBytes("not a pem cert bundle"));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainUndecodableException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"));
    }
}
