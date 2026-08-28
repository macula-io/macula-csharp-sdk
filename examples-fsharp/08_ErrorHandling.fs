module ErrorHandling

open System
open Macula
open Macula.Connection
open Macula.Content
open Macula.Frame
open Macula.Identity
open Macula.Streaming

/// Every error shape this SDK actually produces, in one place: a BOLT#4
/// CallResponse.Error (a normal return value, not an exception), a
/// client-side TimeoutException, a ContentTransferException, and a
/// StreamHandle.RecvStreamException.
let run () =
    task {
        let identity = KeyPair.GenerateWithDefaultPuzzle()
        let! session = Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki)
        let realm = Array.zeroCreate<byte> 32
        let deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000L

        // 1. CallResponse.Error -- the station DID answer, just with a
        // BOLT#4 error. CallAsync only throws for transport/timeout
        // failures; an application-level ERROR is a normal, non-exceptional
        // return value.
        let! response =
            session.CallAsync(
                "macula_csharp_sdk.examples_fsharp.no_such_procedure", realm, Value.Text "hi", deadlineMs, TimeSpan.FromSeconds 10.0)
        match response with
        | :? CallResponse.Error as e ->
            printfn "1. CallResponse.Error: code=%d name=%s -- check Bolt4Code.FromValue(e.Code).IsRetryable before retrying" e.Code e.Name
        | _ -> ()

        // 2. TimeoutException -- an unrealistically short client-side
        // deadline. The station never gets the chance to answer within
        // 1ms of real network latency.
        try
            let! _ = session.CallAsync("macula_csharp_sdk.examples_fsharp.anything", realm, Value.Null, deadlineMs, TimeSpan.FromMilliseconds 1.0)
            printfn "2. (no timeout this time -- the round trip beat 1ms, rare but possible)"
        with :? TimeoutException as te ->
            printfn "2. TimeoutException: %s" te.Message

        // 3. ContentTransfer.ContentTransferException -- fetching content
        // that was never stored. Reason is a typed enum, not just a
        // message string.
        try
            let madeUpMcid = ManifestBuilder.BlockMcid(Text.Encoding.UTF8.GetBytes "never stored")
            let! _ = ContentTransfer.GetAsync(session, madeUpMcid, identity)
            ()
        with :? ContentTransfer.ContentTransferException as cte ->
            printfn "3. ContentTransferException: reason=%A message=%s" cte.Reason cte.Message

        // 4. StreamHandle.RecvStreamException -- opening a stream against a
        // procedure nothing has advertised. The station replies with an
        // explicit STREAM_ERROR almost immediately, not silence.
        try
            let! handle =
                StreamHandle.OpenAsync(
                    session, "macula_csharp_sdk.examples_fsharp.no_such_stream", realm, StreamMode.ClientStream, Value.Null, deadlineMs, identity)
            do! handle.CloseSendAsync identity
            let! _ = handle.AwaitReplyAsync(TimeSpan.FromSeconds 5.0)
            ()
        with :? StreamHandle.RecvStreamException as rse ->
            printfn "4. StreamHandle.RecvStreamException: kind=%A code=%s" rse.Kind rse.Code

        do! session.CloseAsync()
    }
