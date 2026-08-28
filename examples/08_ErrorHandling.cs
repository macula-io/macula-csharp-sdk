using Macula.Connection;
using Macula.Content;
using Macula.Frame;
using Macula.Identity;
using Macula.Streaming;

namespace Macula.Examples;

/// <summary>
/// Every error shape this SDK actually produces, in one place: a BOLT#4
/// CallResponse.Error (a normal return value, not an exception), a
/// client-side TimeoutException, a ContentTransferException, and a
/// StreamHandle.RecvStreamException.
/// </summary>
public static class ErrorHandling
{
    public static async Task RunAsync()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki);
        var realm = new byte[32];
        var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;

        // 1. CallResponse.Error -- the station DID answer, just with a BOLT#4
        // error. CallAsync only throws for transport/timeout failures; an
        // application-level ERROR is a normal, non-exceptional return value.
        var response = await session.CallAsync(
            "macula_csharp_sdk.examples.no_such_procedure", realm, Value.Text("hi"), deadlineMs, TimeSpan.FromSeconds(10));
        if (response is CallResponse.Error e)
        {
            Console.WriteLine($"1. CallResponse.Error: code={e.Code} name={e.Name} -- check e.Code against Bolt4Code.FromValue(e.Code).IsRetryable before retrying");
        }

        // 2. TimeoutException -- an unrealistically short client-side deadline.
        // The station never gets the chance to answer within 1ms of real
        // network latency.
        try
        {
            await session.CallAsync("macula_csharp_sdk.examples.anything", realm, Value.Null, deadlineMs, TimeSpan.FromMilliseconds(1));
            Console.WriteLine("2. (no timeout this time -- the round trip beat 1ms, rare but possible)");
        }
        catch (TimeoutException te)
        {
            Console.WriteLine($"2. TimeoutException: {te.Message}");
        }

        // 3. ContentTransfer.ContentTransferException -- fetching content that
        // was never stored. Reason is a typed enum, not just a message string.
        try
        {
            var madeUpMcid = ManifestBuilder.BlockMcid("never stored"u8.ToArray());
            await ContentTransfer.GetAsync(session, madeUpMcid, identity);
        }
        catch (ContentTransfer.ContentTransferException cte)
        {
            Console.WriteLine($"3. ContentTransferException: reason={cte.Reason} message={cte.Message}");
        }

        // 4. StreamHandle.RecvStreamException -- opening a stream against a
        // procedure nothing has advertised. The station replies with an
        // explicit STREAM_ERROR almost immediately, not silence.
        try
        {
            var handle = await StreamHandle.OpenAsync(
                session, "macula_csharp_sdk.examples.no_such_stream", realm, StreamMode.ClientStream, Value.Null, deadlineMs, identity);
            await handle.CloseSendAsync(identity);
            await handle.AwaitReplyAsync(TimeSpan.FromSeconds(5));
        }
        catch (StreamHandle.RecvStreamException rse)
        {
            Console.WriteLine($"4. StreamHandle.RecvStreamException: kind={rse.Kind} code={rse.Code}");
        }
    }
}
