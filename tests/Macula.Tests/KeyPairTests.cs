using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Reuses the exact same reference vectors as macula-rust-sdk's
/// identity.rs, captured directly from a real, random
/// `crypto:generate_key(eddsa, ed25519)` / `crypto:sign/4` /
/// `crypto:hash(sha256, Pub)` in macula-io/macula's own `rebar3 shell` --
/// not independently derived. Passing these proves BouncyCastle's Ed25519
/// implementation agrees with Erlang's `crypto` module byte-for-byte,
/// without needing a live station for this specific check.
/// </summary>
public class KeyPairTests
{
    // Extracted programmatically from macula-rust-sdk's src/identity.rs
    // (not hand-transcribed) -- captured directly from a real
    // crypto:generate_key/crypto:sign/crypto:hash(sha256) in macula-io/macula's
    // own rebar3 shell. See that module's doc comment for provenance.
    private const string VectorPub = "B966A9812649C3D5542FF54954FE090C43FDA6574FE48A0DD326626CFAD29A83";
    private const string VectorPriv = "457F45FF5A09E172ED15CB20D6CB26B51AD15ED7308C12D478E8631F9CA03D4F";
    private const string VectorMsg = "6D6163756C612D76322D6672616D650068656C6C6F20776F726C64";
    private const string VectorSig = "E8605CF0387CDFCDD88308A0E40A1DCB83402864C335A64D44431DC8ABC5E7E4FF16CA0C56231B32EEB312C4F89F20B6BA76280AFD622983E9D8BC5F4456AC0B";
    private const string VectorPuzzleEvidence = "09D48C91CB46513ED2580BDCEA87C40DA508D4E50EC3DF2F701AFC55D1C5C0B2";
    private const uint VectorLeadingZeroBits = 4;

    [Fact]
    public void Seed_derives_the_reference_pubkey()
    {
        var kp = KeyPair.FromSeedBytes(Convert.FromHexString(VectorPriv));
        Assert.Equal(Convert.FromHexString(VectorPub), kp.PublicBytes());
    }

    [Fact]
    public void Signature_matches_the_reference_byte_for_byte()
    {
        var kp = KeyPair.FromSeedBytes(Convert.FromHexString(VectorPriv));
        var msg = Convert.FromHexString(VectorMsg);
        var sig = kp.Sign(msg);
        Assert.Equal(Convert.FromHexString(VectorSig), sig);
    }

    [Fact]
    public void Verify_accepts_the_reference_signature()
    {
        var pubkey = Convert.FromHexString(VectorPub);
        var msg = Convert.FromHexString(VectorMsg);
        var sig = Convert.FromHexString(VectorSig);
        Assert.True(KeyPair.Verify(msg, sig, pubkey));
    }

    [Fact]
    public void Verify_rejects_a_tampered_message()
    {
        var pubkey = Convert.FromHexString(VectorPub);
        var sig = Convert.FromHexString(VectorSig);
        Assert.False(KeyPair.Verify("not the original message"u8.ToArray(), sig, pubkey));
    }

    [Fact]
    public void Verify_rejects_a_structurally_invalid_pubkey_without_throwing()
    {
        var bogusPubkey = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        var msg = Convert.FromHexString(VectorMsg);
        var sig = Convert.FromHexString(VectorSig);
        Assert.False(KeyPair.Verify(msg, sig, bogusPubkey));
    }

    [Fact]
    public void Puzzle_evidence_matches_the_reference()
    {
        var pubkey = Convert.FromHexString(VectorPub);
        Assert.Equal(Convert.FromHexString(VectorPuzzleEvidence), Puzzle.Evidence(pubkey));
    }

    [Fact]
    public void Puzzle_valid_matches_the_reference_leading_zero_count()
    {
        var pubkey = Convert.FromHexString(VectorPub);
        Assert.True(Puzzle.IsValid(pubkey, VectorLeadingZeroBits));
        Assert.False(Puzzle.IsValid(pubkey, VectorLeadingZeroBits + 1));
        Assert.True(Puzzle.IsValid(pubkey, 0));
    }

    [Fact]
    public void Generate_with_default_puzzle_produces_a_valid_identity()
    {
        // A real grind, not a fixture -- proves the loop terminates and its
        // result actually satisfies what it's grinding for. Sub-millisecond
        // at the default difficulty per the reference's own comment.
        var kp = KeyPair.GenerateWithDefaultPuzzle();
        Assert.True(Puzzle.IsValid(kp.PublicBytes(), Puzzle.DefaultDifficulty));
    }

    [Fact]
    public void Save_and_load_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"macula-test-{Guid.NewGuid():N}.key");
        try
        {
            var original = KeyPair.FromSeedBytes(Convert.FromHexString(VectorPriv));
            original.Save(path);

            var loaded = KeyPair.Load(path);
            Assert.Equal(original.PublicBytes(), loaded.PublicBytes());
            Assert.Equal(original.PrivateBytes(), loaded.PrivateBytes());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Saved_key_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"macula-test-{Guid.NewGuid():N}.key");
        try
        {
            KeyPair.Generate().Save(path);
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_a_corrupted_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"macula-test-{Guid.NewGuid():N}.key");
        try
        {
            File.WriteAllText(path, "not a key file");
            Assert.Throws<LoadKeyException>(() => KeyPair.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_a_tampered_pubkey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"macula-test-{Guid.NewGuid():N}.key");
        try
        {
            KeyPair.Generate().Save(path);

            var blob = File.ReadAllBytes(path);
            var pubOffset = "macula-v2-key\0".Length;
            blob[pubOffset] ^= 0xFF;
            File.WriteAllBytes(path, blob);

            Assert.Throws<LoadKeyException>(() => KeyPair.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
