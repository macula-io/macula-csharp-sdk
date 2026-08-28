namespace Macula.Bolt4;

/// <summary>
/// The BOLT#4 error taxonomy an ERROR frame's `code` field carries -- 17
/// entries adapted from Lightning Network's onion-failure codes. Matches
/// `macula_bolt4.erl`.
/// </summary>
public enum RetryPolicy
{
    None,
    DifferentPath,
    SamePathAfterBackoff,
    CallerRecomputeWithLookup,
    Application,
    CallerRecompute,
    CallerExtendsDeadline,
    ExponentialBackoff,
    CryptoDrop,
    LogAndCaution,
}

public readonly record struct Bolt4Code(byte Value, string Name, RetryPolicy Retry)
{
    public static readonly Bolt4Code Ok = new(0x00, "ok", RetryPolicy.None);
    public static readonly Bolt4Code UnknownNextPeer = new(0x01, "unknown_next_peer", RetryPolicy.DifferentPath);
    public static readonly Bolt4Code TemporaryRelayFailure = new(0x02, "temporary_relay_failure", RetryPolicy.SamePathAfterBackoff);
    public static readonly Bolt4Code RelayDisabled = new(0x03, "relay_disabled", RetryPolicy.DifferentPath);
    public static readonly Bolt4Code NodeNotFoundAtTargetRelay = new(0x04, "node_not_found_at_target_relay", RetryPolicy.CallerRecomputeWithLookup);
    public static readonly Bolt4Code TargetRealmRefused = new(0x05, "target_realm_refused", RetryPolicy.Application);
    public static readonly Bolt4Code LoopDetected = new(0x06, "loop_detected", RetryPolicy.CallerRecompute);
    public static readonly Bolt4Code ExpiryTooSoon = new(0x07, "expiry_too_soon", RetryPolicy.CallerExtendsDeadline);
    public static readonly Bolt4Code UpstreamCongestion = new(0x08, "upstream_congestion", RetryPolicy.ExponentialBackoff);
    public static readonly Bolt4Code InvalidPathHeader = new(0x09, "invalid_path_header", RetryPolicy.CallerRecompute);
    public static readonly Bolt4Code CryptoPuzzleInvalid = new(0x0A, "crypto_puzzle_invalid", RetryPolicy.CryptoDrop);
    public static readonly Bolt4Code RealmNotAuthoritativeHere = new(0x0B, "realm_not_authoritative_here", RetryPolicy.CallerRecomputeWithLookup);
    public static readonly Bolt4Code Tombstoned = new(0x0C, "tombstoned", RetryPolicy.Application);
    public static readonly Bolt4Code PayloadTooLarge = new(0x0D, "payload_too_large", RetryPolicy.Application);
    public static readonly Bolt4Code SignatureInvalid = new(0x0E, "signature_invalid", RetryPolicy.CryptoDrop);
    public static readonly Bolt4Code UnknownError = new(0x0F, "unknown_error", RetryPolicy.LogAndCaution);
    public static readonly Bolt4Code Unauthorized = new(0x10, "unauthorized", RetryPolicy.Application);

    private static readonly Bolt4Code[] All =
    {
        Ok, UnknownNextPeer, TemporaryRelayFailure, RelayDisabled, NodeNotFoundAtTargetRelay,
        TargetRealmRefused, LoopDetected, ExpiryTooSoon, UpstreamCongestion, InvalidPathHeader,
        CryptoPuzzleInvalid, RealmNotAuthoritativeHere, Tombstoned, PayloadTooLarge,
        SignatureInvalid, UnknownError, Unauthorized,
    };

    public static Bolt4Code FromValue(byte value) =>
        All.FirstOrDefault(c => c.Value == value, new Bolt4Code(value, "unknown_error", RetryPolicy.LogAndCaution));
}
