using Macula.Connection;
using Macula.Frame;
using Macula.Identity;
using Macula.Streaming;

namespace Macula.Examples;

/// <summary>
/// Streaming RPC, caller role: open a stream, push a chunk, half-close,
/// then wait for the terminal STREAM_REPLY. Against a made-up procedure
/// nothing actually serves, so this shows the failure path: the station
/// replies with an explicit STREAM_ERROR (unknown_next_peer) almost
/// immediately, the same way it would ERROR a CALL to an unadvertised
/// procedure -- not silence/a timeout as one might first guess.
/// </summary>
public static class StreamOpenCaller
{
    public static async Task RunAsync()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki);

        var realm = new byte[32];
        Random.Shared.NextBytes(realm);
        var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
        var handle = await StreamHandle.OpenAsync(
            session,
            "macula_csharp_sdk.examples.no_such_stream",
            realm,
            StreamMode.ClientStream,
            Value.Null,
            deadlineMs,
            identity);

        await handle.SendDataAsync(StreamEncoding.Raw, Value.Bytes("hello from macula-dotnet-sdk"u8.ToArray()), identity);
        await handle.CloseSendAsync(identity);
        Console.WriteLine("sent one chunk and half-closed");

        try
        {
            var (payload, respondedBy) = await handle.AwaitReplyAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"got a STREAM_REPLY: payload={payload} responded_by={Convert.ToHexStringLower(respondedBy)}");
        }
        catch (StreamHandle.RecvStreamException e) when (e.Kind == StreamHandle.RecvErrorKind.PeerAborted)
        {
            Console.WriteLine($"station aborted the stream: {e.Message} -- expected, since nothing advertises this made-up procedure");
        }
        catch (TimeoutException)
        {
            Console.WriteLine("no reply within 5s");
        }
    }
}
