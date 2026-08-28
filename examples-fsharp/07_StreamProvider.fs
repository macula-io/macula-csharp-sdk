module StreamProvider

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity
open Macula.Streaming

/// Streaming RPC, provider role: one session advertises a procedure and
/// accepts inbound STREAM_OPENs for it; a second session dials in and
/// pulls data from it (server_stream mode: the provider pushes chunks).
/// Two Sessions in one process, same as the unary-RPC provider example.
let run () =
    task {
        let providerIdentity = KeyPair.GenerateWithDefaultPuzzle()
        let callerIdentity = KeyPair.GenerateWithDefaultPuzzle()

        let! providerSession = Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki)
        let! callerSession = Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki)

        let realm = Array.zeroCreate<byte> 32
        let procedure = sprintf "macula_csharp_sdk.examples_fsharp.stream_provider.%s" (Guid.NewGuid().ToString "N")

        do! providerSession.AdvertiseAsync(AdvertiseSpec(Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId()))
        printfn "advertised %s" procedure
        do! Threading.Tasks.Task.Delay 500

        let acceptTask = StreamHandle.AcceptAsync(providerSession, TimeSpan.FromSeconds 10.0)

        let deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000L
        let! callerHandle =
            StreamHandle.OpenAsync(callerSession, procedure, realm, StreamMode.ServerStream, Value.Null, deadlineMs, callerIdentity)

        let! (providerHandle, openInfo) = acceptTask
        printfn "provider accepted stream_open for procedure=%s mode=%A" openInfo.Procedure openInfo.Mode

        do!
            providerHandle.SendDataAsync(
                StreamEncoding.Raw, Value.Bytes(Text.Encoding.UTF8.GetBytes "hello from the provider"), providerIdentity)
        do! providerHandle.CloseSendAsync providerIdentity

        let! item = callerHandle.RecvAsync(TimeSpan.FromSeconds 5.0)
        match item with
        | :? StreamItem.Data as data -> printfn "caller received: %s" (Text.Encoding.UTF8.GetString(data.Body.AsBytes()))
        | _ -> printfn "unexpected item shape"

        let! eof = callerHandle.RecvAsync(TimeSpan.FromSeconds 5.0)
        printfn "caller saw %s" (if eof = StreamItem.Eof then "end-of-stream" else "something unexpected")

        do! providerSession.CloseAsync()
        do! callerSession.CloseAsync()
    }
