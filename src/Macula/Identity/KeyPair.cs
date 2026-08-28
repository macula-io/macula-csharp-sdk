using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Macula.Identity;

/// <summary>
/// An Ed25519 keypair. The public half IS the macula NodeId -- there is no
/// separate account/identity layer underneath it. Matches
/// <c>macula_identity.erl</c> (macula-io/macula) and the sibling Rust SDK's
/// <c>identity.rs</c> byte-for-byte; this module's test vectors are the
/// exact same ones captured from a real <c>rebar3 shell</c> that
/// macula-rust-sdk uses, not independently derived.
/// </summary>
public sealed class KeyPair
{
    private static readonly byte[] KeyFileMagic = "macula-v2-key\0"u8.ToArray();

    private readonly Ed25519PrivateKeyParameters _privateKey;
    private readonly Ed25519PublicKeyParameters _publicKey;

    private KeyPair(Ed25519PrivateKeyParameters privateKey)
    {
        _privateKey = privateKey;
        _publicKey = privateKey.GeneratePublicKey();
    }

    /// <summary>
    /// Generate a fresh keypair. Does NOT grind a puzzle -- the resulting
    /// identity will be silently rejected by any station that enforces
    /// puzzle admission (which is every station in practice). Prefer
    /// <see cref="GenerateWithDefaultPuzzle"/> unless this identity will
    /// never dial a real station (e.g. a unit test).
    /// </summary>
    public static KeyPair Generate() => new(new Ed25519PrivateKeyParameters(new SecureRandom()));

    /// <summary>
    /// Generate a keypair, grinding fresh candidates until
    /// <see cref="Puzzle.IsValid"/> holds. A one-time cost paid once per
    /// identity -- never redo this per connection.
    /// </summary>
    public static KeyPair GenerateWithPuzzle(uint difficulty)
    {
        while (true)
        {
            var candidate = Generate();
            if (Puzzle.IsValid(candidate.PublicBytes(), difficulty))
            {
                return candidate;
            }
        }
    }

    public static KeyPair GenerateWithDefaultPuzzle() => GenerateWithPuzzle(Puzzle.DefaultDifficulty);

    /// <summary>
    /// Reconstruct a keypair from its 32-byte RFC 8032 seed. Deterministic --
    /// the same seed always yields the same public key and, for a given
    /// message, the same signature (Ed25519 has no signing randomness).
    /// </summary>
    public static KeyPair FromSeedBytes(byte[] seed)
    {
        if (seed.Length != 32)
        {
            throw new ArgumentException("Ed25519 seed must be exactly 32 bytes", nameof(seed));
        }
        return new KeyPair(new Ed25519PrivateKeyParameters(seed, 0));
    }

    /// <summary>The public key -- also this identity's macula NodeId.</summary>
    public byte[] PublicBytes() => _publicKey.GetEncoded();

    /// <summary>The 32-byte seed. Matches <c>macula_identity:private/1</c>.</summary>
    public byte[] PrivateBytes() => _privateKey.GetEncoded();

    /// <summary>Alias for <see cref="PublicBytes"/> -- NodeId == public key.</summary>
    public byte[] NodeId() => PublicBytes();

    /// <summary>
    /// Sign <paramref name="msg"/> with this identity. Callers add their own
    /// domain separation by prefixing <paramref name="msg"/> (see the
    /// frame-signing domains in the wire-protocol spec) -- this method
    /// itself is raw Ed25519, matching <c>macula_identity:sign/2</c> exactly.
    /// </summary>
    public byte[] Sign(byte[] msg)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(msg, 0, msg.Length);
        return signer.GenerateSignature();
    }

    /// <summary>This identity's puzzle evidence -- see <see cref="Puzzle.Evidence"/>.</summary>
    public byte[] PuzzleEvidence() => Puzzle.Evidence(PublicBytes());

    /// <summary>
    /// Verify <paramref name="sig"/> over <paramref name="msg"/> against
    /// <paramref name="pubkey"/>. A structurally invalid public key is
    /// treated as "verification failed", not a separate error -- it could
    /// not have produced a valid signature either way.
    /// </summary>
    public static bool Verify(byte[] msg, byte[] sig, byte[] pubkey)
    {
        if (pubkey.Length != 32 || sig.Length != 64)
        {
            return false;
        }

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(pubkey, 0));
            verifier.BlockUpdate(msg, 0, msg.Length);
            return verifier.VerifySignature(sig);
        }
        catch (Exception)
        {
            // BouncyCastle throws on a structurally malformed key/signature
            // rather than just returning false -- collapse that into the
            // same "verification failed" outcome the reference contract
            // promises, since the caller has no separate error path to
            // route a parse failure to.
            return false;
        }
    }

    /// <summary>
    /// Save this keypair to <paramref name="path"/>, atomically (write to a
    /// sibling temp file, then move) with owner-only permissions on Unix --
    /// matching <c>macula_identity:save/2</c>'s own file format exactly: a
    /// 14-byte magic header, then the 32-byte public key, then the 32-byte
    /// private seed.
    ///
    /// This raw-file format is a testing/parity convenience matching the
    /// Erlang reference. A real mobile/desktop app should use platform
    /// secure storage (Keychain, Keystore, DPAPI) instead of this format
    /// directly.
    /// </summary>
    public void Save(string path)
    {
        var blob = new byte[KeyFileMagic.Length + 64];
        KeyFileMagic.CopyTo(blob, 0);
        PublicBytes().CopyTo(blob, KeyFileMagic.Length);
        PrivateBytes().CopyTo(blob, KeyFileMagic.Length + 32);

        var tmpPath = path + ".tmp";
        File.WriteAllBytes(tmpPath, blob);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tmpPath, path, overwrite: true);
    }

    /// <summary>Load a keypair previously written by <see cref="Save"/>.</summary>
    public static KeyPair Load(string path)
    {
        var blob = File.ReadAllBytes(path);
        var expectedLen = KeyFileMagic.Length + 64;
        if (blob.Length != expectedLen || !blob.AsSpan(0, KeyFileMagic.Length).SequenceEqual(KeyFileMagic))
        {
            throw new LoadKeyException("key file has the wrong magic header or length");
        }

        var storedPub = blob.AsSpan(KeyFileMagic.Length, 32).ToArray();
        var storedPriv = blob.AsSpan(KeyFileMagic.Length + 32, 32).ToArray();

        var keyPair = FromSeedBytes(storedPriv);
        if (!keyPair.PublicBytes().AsSpan().SequenceEqual(storedPub))
        {
            throw new LoadKeyException(
                "stored public key does not match the one derived from the stored private key");
        }
        return keyPair;
    }
}

public sealed class LoadKeyException : Exception
{
    public LoadKeyException(string message) : base(message) { }
}
