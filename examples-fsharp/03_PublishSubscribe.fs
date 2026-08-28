module PublishSubscribe

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity

/// Subscribe to a topic, publish to it, and receive the resulting EVENT --
/// a subscriber does receive its own publish, delivered_via "direct".
///
/// <see cref="Session.RecvEventAsync"/> errors on the first non-EVENT
/// frame rather than silently skipping it, matching the sibling Go/Rust/
/// PHP/C# SDKs. In practice against the real station, that needs a
/// caveat: it periodically sends unprompted `advertise` frames for its
/// own built-in `_content.*` procedures over every connected client's
/// control stream (observed directly building the C# examples, not
/// documented anywhere), so a real caller should loop past frames it
/// doesn't care about rather than call RecvEventAsync exactly once.
let private recvEventSkippingOtherTraffic (session: Session) (timeout: TimeSpan) =
    task {
        use cts = new Threading.CancellationTokenSource(timeout)
        let mutable result = None
        while result.IsNone do
            let! frame = session.RecvAsync(cts.Token)
            match frame with
            | :? Value.MapValue as map ->
                match map.Get "frame_type" with
                | :? Value.TextValue as ft when ft.AsText() = "event" ->
                    result <- Some(EventFrameParsing.Parse frame)
                | _ -> ()
            | _ -> ()
        return result.Value
    }

let run () =
    task {
        let identity = KeyPair.GenerateWithDefaultPuzzle()
        let! session = Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki)

        // A random realm, not the all-zero sentinel: this is a shared
        // public demo station, and realm=zero is exactly the sentinel
        // macula's own content-transfer procedures use, so it sees
        // meaningfully more unrelated traffic than a random realm does.
        let realm = Array.zeroCreate<byte> 32
        Random.Shared.NextBytes realm
        let topic = sprintf "macula_csharp_sdk.examples_fsharp.%s" (Guid.NewGuid().ToString "N")

        do! session.SubscribeAsync(SubscribeSpec(Topic = topic, Realm = realm, Subscriber = identity.NodeId()))
        printfn "subscribed to %s" topic

        do!
            session.PublishAsync(
                PublishSpec(
                    Topic = topic,
                    Realm = realm,
                    Publisher = identity.NodeId(),
                    Seq = 1UL,
                    Payload = Value.Text "hello mesh",
                    PublishedAtMs = uint64 (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
        printfn "published"

        let! evt = recvEventSkippingOtherTraffic session (TimeSpan.FromSeconds 10.0)
        printfn "received EVENT: topic=%s payload=%s delivered_via=%s" evt.Topic (evt.Payload.AsText()) evt.DeliveredVia

        do! session.CloseAsync()
    }
