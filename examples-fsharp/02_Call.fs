module Call

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity

/// Unary RPC caller: CALL a procedure and read back RESULT or ERROR.
let run () =
    task {
        let identity = KeyPair.GenerateWithDefaultPuzzle()
        let! session = Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki)

        // A deliberately-nonexistent procedure, so this example always
        // gets a real (BOLT#4 unknown_next_peer) ERROR to show both
        // outcomes of a CallResponse.
        let realm = Array.zeroCreate<byte> 32
        let deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000L
        let! response =
            session.CallAsync(
                "macula_csharp_sdk.examples_fsharp.no_such_procedure",
                realm,
                Value.Text "hello",
                deadlineMs,
                TimeSpan.FromSeconds 10.0)

        match response with
        | :? CallResponse.Result as r -> printfn "RESULT: %A" r.Payload
        | :? CallResponse.Error as e -> printfn "ERROR: code=%d name=%s detail=%s" e.Code e.Name e.Detail
        | _ -> printfn "unexpected response shape"

        do! session.CloseAsync()
    }
