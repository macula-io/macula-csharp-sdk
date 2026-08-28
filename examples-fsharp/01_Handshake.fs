module Handshake

open System
open Macula.Connection
open Macula.Identity

/// Identity generation, connect, and a clean close -- the smallest
/// possible program that speaks to the mesh.
let run () =
    task {
        // Puzzle-hardened: every station checks this on every CONNECT.
        // Skipping it fails silently -- the QUIC/TLS layer looks healthy,
        // the station just never accepts the HELLO. Sub-millisecond at
        // the default difficulty.
        let identity = KeyPair.GenerateWithDefaultPuzzle()
        printfn "identity node_id = %s" (Convert.ToHexStringLower(identity.NodeId()))

        let! session = Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki)

        printfn "connected -- station node_id = %s" (Convert.ToHexStringLower(session.RemoteInfo.NodeId))
        printfn "accepted = %b" session.RemoteInfo.Accepted

        do! session.CloseAsync() // sends GOODBYE and closes.
    }
