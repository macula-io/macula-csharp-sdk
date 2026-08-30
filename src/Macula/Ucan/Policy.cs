namespace Macula.Ucan;

/// <summary>
/// Describes what a service requires to answer one (realm, procedure):
/// open (any identified caller, the default) or UCAN-gated (the caller's
/// token must verify against RequiredIssuer). Mirrors
/// macula_station_link.erl's own policy shape exactly -- `open |
/// {ucan_required, Issuer}` -- where "Issuer" there is the 32-byte Ed25519
/// public key the gate checks the token's signature against, not a DID
/// string.
///
/// Gating happens BEFORE a handler runs -- see
/// Connection.Session.ServeOneCallGatedAsync -- so a rejected caller never
/// reaches business logic, and an accepted caller's handler never sees the
/// raw token either; the policy layer already did the only thing that
/// mattered with it.
/// </summary>
public sealed record Policy(bool Gated, byte[]? RequiredIssuer)
{
    /// <summary>The default, ungated policy: any identified caller may invoke the procedure, no UCAN token needed. Equivalent to Erlang's `open`.</summary>
    public static readonly Policy Open = new(false, null);

    /// <summary>Builds a UCAN-gated policy: a caller must present a token that verifies (signature, exp, nbf) against issuerPublicKey. Equivalent to Erlang's `{ucan_required, issuerPublicKey}`.</summary>
    public static Policy Required(byte[] issuerPublicKey) => new(true, issuerPublicKey);

    /// <summary>
    /// Applies this policy to an inbound CALL's ucanToken, throwing if the
    /// call is NOT authorized to proceed to lookup/dispatch. An open
    /// policy always passes; a gated policy requires ucanToken to Verify
    /// against RequiredIssuer.
    /// </summary>
    public void Check(byte[] ucanToken)
    {
        if (!Gated)
        {
            return;
        }
        if (ucanToken.Length == 0)
        {
            throw new UcanToken.NoTokenException();
        }
        UcanToken.Verify(ucanToken, RequiredIssuer!);
    }
}
