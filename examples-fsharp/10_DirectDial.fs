module DirectDialExample

open System
open Macula
open Macula.Connection
open Macula.Dht
open Macula.Frame
open Macula.Identity

/// Direct-dial: a provider advertises via DirectDial.AdvertiseDirectAsync
/// (publishing a signed procedure_advertisement DHT record AND the plain
/// ADVERTISE), a separate session resolves that record and dials the
/// provider's station directly -- reaching it without depending on
/// ordinary advertise-gossip having propagated a route between whichever
/// two stations happen to be involved. Two Sessions, two identities (this
/// fleet kicks whichever connection reuses an identity second).
let run () =
    task {
        let providerIdentity = KeyPair.GenerateWithDefaultPuzzle()
        let callerIdentity = KeyPair.GenerateWithDefaultPuzzle()

        let! providerSession = Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki)
        let! resolverSession = Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki)

        let realm = Array.zeroCreate<byte> 32
        let procedure = sprintf "macula_dotnet_sdk.examples_fsharp.direct_dial.%s" (Guid.NewGuid().ToString "N")

        do! DirectDial.AdvertiseDirectAsync(providerSession, providerIdentity, realm, procedure, TimeSpan.FromHours 1.0)
        printfn "provider advertised %s (plain + direct)" procedure

        let lookup =
            CallLookup(fun _realm proc ->
                if proc = procedure then CallHandler(fun payload -> Threading.Tasks.Task.FromResult payload) else null)
        let serveTask = providerSession.ServeOneCallAsync(lookup, TimeSpan.FromSeconds 15.0)

        let! resolved = DirectDial.ResolveAsync(resolverSession, realm, procedure)
        printfn "resolved %s -> station=%s host=%s port=%d" procedure (Convert.ToHexStringLower resolved.Station) resolved.Host resolved.Port

        let! response = DirectDial.CallAsync(resolverSession, callerIdentity, realm, procedure, Value.Text "hello direct-dial", TimeSpan.FromSeconds 10.0)
        do! serveTask

        match response with
        | :? CallResponse.Result as r -> printfn "caller got real RESULT through direct-dial: %A" r.Payload
        | other -> printfn "caller got ERROR: %A" other

        do! providerSession.CloseAsync()
        do! resolverSession.CloseAsync()
    }
