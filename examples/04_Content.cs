using Macula.Connection;
using Macula.Content;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>Content transfer: put/get, both single-block (small data) and chunked (data over 256 KiB, with a manifest).</summary>
public static class Content
{
    public static async Task RunAsync()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki);

        // Single block: fits in one chunk, addressed purely by content hash.
        var small = "hello from macula-dotnet"u8.ToArray();
        var smallMcid = await ContentTransfer.PutAsync(session, small, "greeting.txt", identity);
        Console.WriteLine($"put single block, mcid={Convert.ToHexStringLower(smallMcid)} (chunked={Mcid.IsChunked(smallMcid)})");

        var fetchedSmall = await ContentTransfer.GetAsync(session, smallMcid, identity);
        Console.WriteLine($"got it back: {System.Text.Encoding.UTF8.GetString(fetchedSmall)}");

        // Chunked: 3 chunks at the default 256 KiB chunk size, uploaded
        // sequentially over one dedicated stream, then a manifest.
        var big = new byte[ManifestBuilder.DefaultChunkSize * 2 + 12_345];
        Random.Shared.NextBytes(big);
        var bigMcid = await ContentTransfer.PutAsync(session, big, "random-blob.bin", identity);
        Console.WriteLine($"put chunked content, mcid={Convert.ToHexStringLower(bigMcid)} (chunked={Mcid.IsChunked(bigMcid)})");

        var fetchedBig = await ContentTransfer.GetAsync(session, bigMcid, identity);
        Console.WriteLine($"got {fetchedBig.Length} bytes back, matches original: {fetchedBig.AsSpan().SequenceEqual(big)}");
    }
}
