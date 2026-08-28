module RpcProvider

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity

/// Unary RPC, provider role: one session advertises a procedure and
/// serves inbound CALLs for it; a second session (a different identity,
/// standing in for a separate caller) dials in and calls it. Two
/// Sessions in one process -- no process-splitting needed, unlike an FFI
/// binding over a cgo-backed SDK would require.
let run () =
    task {
        let providerIdentity = KeyPair.GenerateWithDefaultPuzzle()
        let callerIdentity = KeyPair.GenerateWithDefaultPuzzle()

        let! providerSession = Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki)
        let! callerSession = Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki)

        let realm = Array.zeroCreate<byte> 32
        let procedure = sprintf "macula_csharp_sdk.examples_fsharp.add.%s" (Guid.NewGuid().ToString "N")

        do! providerSession.AdvertiseAsync(AdvertiseSpec(Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId()))
        printfn "advertised %s" procedure
        do! Threading.Tasks.Task.Delay 500 // give the station a moment to register the advertisement

        let handler =
            CallHandler(fun payload ->
                task {
                    match payload with
                    | :? Value.MapValue as map ->
                        match map.Get "a", map.Get "b" with
                        | (:? Value.UIntValue as a), (:? Value.UIntValue as b) -> return Value.UInt(a.Value + b.Value)
                        | _ -> return raise (CallHandlerException "missing or non-integer field \"a\" or \"b\"")
                    | _ -> return raise (CallHandlerException "payload is not a map")
                })

        let lookup =
            CallLookup(fun _realm proc -> if proc = procedure then handler else null)

        let serveTask = providerSession.ServeOneCallAsync(lookup, TimeSpan.FromSeconds 15.0)

        let payload =
            Value.Map [|
                Collections.Generic.KeyValuePair(Value.Text "a", Value.UInt 3UL)
                Collections.Generic.KeyValuePair(Value.Text "b", Value.UInt 4UL)
            |]
        let deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000L
        let! response = callerSession.CallAsync(procedure, realm, payload, deadlineMs, TimeSpan.FromSeconds 10.0)

        do! serveTask

        match response with
        | :? CallResponse.Result as r -> printfn "caller got RESULT: %A" r.Payload
        | other -> printfn "caller got: %A" other

        do! providerSession.CloseAsync()
        do! callerSession.CloseAsync()
    }
