module Content

open System
open System.Text
open Macula.Connection
open Macula.Content
open Macula.Identity

/// Content transfer: put/get, both single-block (small data) and chunked
/// (data over 256 KiB, with a manifest).
let run () =
    task {
        let identity = KeyPair.GenerateWithDefaultPuzzle()
        let! session = Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki)

        // Single block: fits in one chunk, addressed purely by content hash.
        let small = Encoding.UTF8.GetBytes "hello from macula-dotnet-sdk (F#)"
        let! smallMcid = ContentTransfer.PutAsync(session, small, "greeting.txt", identity)
        printfn "put single block, mcid=%s (chunked=%b)" (Convert.ToHexStringLower smallMcid) (Mcid.IsChunked smallMcid)

        let! fetchedSmall = ContentTransfer.GetAsync(session, smallMcid, identity)
        printfn "got it back: %s" (Encoding.UTF8.GetString fetchedSmall)

        // Chunked: 3 chunks at the default 256 KiB chunk size, uploaded
        // sequentially over one dedicated stream, then a manifest.
        let big = Array.zeroCreate<byte> (ManifestBuilder.DefaultChunkSize * 2 + 12_345)
        Random.Shared.NextBytes big
        let! bigMcid = ContentTransfer.PutAsync(session, big, "random-blob.bin", identity)
        printfn "put chunked content, mcid=%s (chunked=%b)" (Convert.ToHexStringLower bigMcid) (Mcid.IsChunked bigMcid)

        let! fetchedBig = ContentTransfer.GetAsync(session, bigMcid, identity)
        printfn "got %d bytes back, matches original: %b" fetchedBig.Length (fetchedBig = big)

        do! session.CloseAsync()
    }
