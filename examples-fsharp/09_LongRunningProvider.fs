module LongRunningProvider

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity

/// Unary RPC provider serving MULTIPLE calls over its lifetime -- the
/// realistic "long-running service" shape, not the "serve exactly one
/// call then exit" shape example 06 shows. A real service loops
/// Session.ServeOneCallAsync indefinitely (or until a CancellationToken
/// fires); this example bounds it to 3 calls purely so it terminates on
/// its own.
let run () =
    task {
        let providerIdentity = KeyPair.GenerateWithDefaultPuzzle()
        let callerIdentity = KeyPair.GenerateWithDefaultPuzzle()

        let! providerSession = Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki)
        let! callerSession = Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki)

        let realm = Array.zeroCreate<byte> 32
        let procedure = sprintf "macula_csharp_sdk.examples_fsharp.counter.%s" (Guid.NewGuid().ToString "N")

        do! providerSession.AdvertiseAsync(AdvertiseSpec(Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId()))
        printfn "advertised %s" procedure
        do! Threading.Tasks.Task.Delay 500 // give the station a moment to register the advertisement

        // ServeOneCallAsync processes exactly one call at a time and this
        // loop awaits each iteration before starting the next, so at most
        // one handler invocation is ever in flight -- a plain mutable
        // counter is enough, no Interlocked needed.
        let mutable callCount = 0

        let handler =
            CallHandler(fun _payload ->
                task {
                    callCount <- callCount + 1
                    return Value.Text(sprintf "call #%d served" callCount)
                })

        let lookup = CallLookup(fun _realm proc -> if proc = procedure then handler else null)

        // The serve loop and the caller loop run concurrently: the
        // provider is genuinely idle between calls, waiting on the next
        // inbound CALL, exactly like a real always-on service would be.
        let serveTask =
            task {
                for _ in 1 .. 3 do
                    do! providerSession.ServeOneCallAsync(lookup, TimeSpan.FromSeconds 15.0)
            }

        for _ in 1 .. 3 do
            let deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000L
            let! response = callerSession.CallAsync(procedure, realm, Value.Null, deadlineMs, TimeSpan.FromSeconds 10.0)
            match response with
            | :? CallResponse.Result as r -> printfn "caller got: %s" (r.Payload.AsText())
            | _ -> ()

        do! serveTask
        printfn "provider served %d calls over its lifetime, not just one" callCount

        do! providerSession.CloseAsync()
        do! callerSession.CloseAsync()
    }
