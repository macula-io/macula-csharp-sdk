using System.Numerics;
using System.Security.Cryptography;

namespace Macula.Identity;

/// <summary>
/// The S/Kademlia Sybil-defense puzzle: an identity is "hardened" once its
/// public key's SHA-256 hash has at least N leading zero bits. Raises the
/// cost of minting identities in bulk, not of connecting with one that
/// already exists -- grinding happens once at identity creation, never per
/// connection. Every station checks this on every CONNECT/HELLO; skipping
/// it produces a connection that reports healthy while the station silently
/// rejects the application-layer HELLO.
/// </summary>
public static class Puzzle
{
    /// <summary>Matches `?DEFAULT_PUZZLE_DIFFICULTY` in macula_identity.erl.</summary>
    public const uint DefaultDifficulty = 8;

    /// <summary>SHA-256(pubkey) -- the proof-of-work output the puzzle measures.</summary>
    public static byte[] Evidence(byte[] pubkey) => SHA256.HashData(pubkey);

    public static bool IsValid(byte[] pubkey, uint difficulty) =>
        CountLeadingZeroBits(Evidence(pubkey)) >= difficulty;

    private static uint CountLeadingZeroBits(ReadOnlySpan<byte> bytes)
    {
        uint count = 0;
        foreach (var b in bytes)
        {
            if (b == 0)
            {
                count += 8;
            }
            else
            {
                count += (uint)BitOperations.LeadingZeroCount(b) - 24;
                break;
            }
        }
        return count;
    }
}
