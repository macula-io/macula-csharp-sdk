using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>Unary RPC caller: CALL a procedure and read back RESULT or ERROR.</summary>
public static class Call
{
    public static async Task RunAsync()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki);

        // A deliberately-nonexistent procedure, so this example always
        // gets a real (BOLT#4 unknown_next_peer) ERROR to show both
        // outcomes of a CallResponse.
        var realm = new byte[32];
        var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
        var response = await session.CallAsync(
            "macula_csharp_sdk.examples.no_such_procedure",
            realm,
            Value.Text("hello"),
            deadlineMs,
            TimeSpan.FromSeconds(10));

        switch (response)
        {
            case CallResponse.Result r:
                Console.WriteLine($"RESULT: {r.Payload}");
                break;
            case CallResponse.Error e:
                Console.WriteLine($"ERROR: code={e.Code} name={e.Name} detail={e.Detail}");
                break;
        }
    }
}
