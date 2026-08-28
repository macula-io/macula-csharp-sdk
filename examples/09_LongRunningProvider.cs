using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>
/// Unary RPC provider serving MULTIPLE calls over its lifetime -- the
/// realistic "long-running service" shape, not the "serve exactly one
/// call then exit" shape example 06 shows. A real service loops
/// <see cref="Session.ServeOneCallAsync"/> indefinitely (or until a
/// CancellationToken fires); this example bounds it to 3 calls purely so
/// it terminates on its own.
/// </summary>
public static class LongRunningProvider
{
    public static async Task RunAsync()
    {
        var providerIdentity = KeyPair.GenerateWithDefaultPuzzle();
        var callerIdentity = KeyPair.GenerateWithDefaultPuzzle();

        await using var providerSession = await Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki);
        await using var callerSession = await Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki);

        var realm = new byte[32];
        var procedure = $"macula_csharp_sdk.examples.counter.{Guid.NewGuid():N}";

        await providerSession.AdvertiseAsync(new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId() });
        Console.WriteLine($"advertised {procedure}");
        await Task.Delay(500); // give the station a moment to register the advertisement

        var callCount = 0;
        CallLookup lookup = (_, proc) =>
        {
            if (proc != procedure)
            {
                return null;
            }
            return async payload =>
            {
                var n = Interlocked.Increment(ref callCount);
                await Task.CompletedTask;
                return Value.Text($"call #{n} served");
            };
        };

        // The serve loop and the caller loop run concurrently: the
        // provider is genuinely idle between calls, waiting on the next
        // inbound CALL, exactly like a real always-on service would be.
        var serveTask = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                await providerSession.ServeOneCallAsync(lookup, TimeSpan.FromSeconds(15));
            }
        });

        for (var i = 0; i < 3; i++)
        {
            var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
            var response = await callerSession.CallAsync(procedure, realm, Value.Null, deadlineMs, TimeSpan.FromSeconds(10));
            if (response is CallResponse.Result r)
            {
                Console.WriteLine($"caller got: {r.Payload.AsText()}");
            }
        }

        await serveTask;
        Console.WriteLine($"provider served {callCount} calls over its lifetime, not just one");
    }
}
