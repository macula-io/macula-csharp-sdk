module StreamOpenCaller

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity
open Macula.Streaming

/// Streaming RPC, caller role: open a stream, push a chunk, half-close,
/// then wait for the terminal STREAM_REPLY. Against a made-up procedure
/// nothing actually serves, so this shows the failure path: the station
/// replies with an explicit STREAM_ERROR (unknown_next_peer) almost
/// immediately, not silence/a timeout as one might first guess.
let run () =
    task {
        let identity = KeyPair.GenerateWithDefaultPuzzle()
        let! session = Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki)

        let realm = Array.zeroCreate<byte> 32
        Random.Shared.NextBytes realm
        let deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000L
        let! handle =
            StreamHandle.OpenAsync(
                session,
                "macula_csharp_sdk.examples_fsharp.no_such_stream",
                realm,
                StreamMode.ClientStream,
                Value.Null,
                deadlineMs,
                identity)

        do! handle.SendDataAsync(StreamEncoding.Raw, Value.Bytes(Text.Encoding.UTF8.GetBytes "hello from macula-dotnet-sdk (F#)"), identity)
        do! handle.CloseSendAsync identity
        printfn "sent one chunk and half-closed"

        try
            let! (payload, respondedBy) = handle.AwaitReplyAsync(TimeSpan.FromSeconds 5.0)
            printfn "got a STREAM_REPLY: payload=%A responded_by=%s" payload (Convert.ToHexStringLower respondedBy)
        with
        | :? StreamHandle.RecvStreamException as e when e.Kind = StreamHandle.RecvErrorKind.PeerAborted ->
            printfn "station aborted the stream: %s -- expected, since nothing advertises this made-up procedure" e.Message
        | :? TimeoutException ->
            printfn "no reply within 5s"

        do! session.CloseAsync()
    }
