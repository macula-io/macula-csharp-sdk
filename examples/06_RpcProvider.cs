using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>
/// Unary RPC, provider role: one session advertises a procedure and
/// serves inbound CALLs for it; a second session (a different identity,
/// standing in for a separate caller) dials in and calls it. Two
/// <see cref="Session"/>s in one process -- no process-splitting needed,
/// unlike an FFI binding over a cgo-backed SDK would require.
/// </summary>
public static class RpcProvider
{
    public static async Task RunAsync()
    {
        var providerIdentity = KeyPair.GenerateWithDefaultPuzzle();
        var callerIdentity = KeyPair.GenerateWithDefaultPuzzle();

        await using var providerSession = await Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki);
        await using var callerSession = await Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki);

        var realm = new byte[32];
        var procedure = $"macula_csharp_sdk.examples.add.{Guid.NewGuid():N}";

        await providerSession.AdvertiseAsync(new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId() });
        Console.WriteLine($"advertised {procedure}");
        await Task.Delay(500); // give the station a moment to register the advertisement

        CallLookup lookup = (_, proc) =>
        {
            if (proc != procedure)
            {
                return null;
            }
            return async payload =>
            {
                if (payload is not Value.MapValue map
                    || map.Get("a") is not Value.UIntValue a
                    || map.Get("b") is not Value.UIntValue b)
                {
                    throw new CallHandlerException("missing or non-integer field \"a\" or \"b\"");
                }
                await Task.CompletedTask;
                return Value.UInt(a.Value + b.Value);
            };
        };

        var serveTask = providerSession.ServeOneCallAsync(lookup, TimeSpan.FromSeconds(15));

        var payload = Value.Map(new[]
        {
            new KeyValuePair<Value, Value>(Value.Text("a"), Value.UInt(3)),
            new KeyValuePair<Value, Value>(Value.Text("b"), Value.UInt(4)),
        });
        var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
        var callTask = callerSession.CallAsync(procedure, realm, payload, deadlineMs, TimeSpan.FromSeconds(10));

        await Task.WhenAll(serveTask, callTask);

        var response = callTask.Result;
        Console.WriteLine(response is CallResponse.Result r
            ? $"caller got RESULT: {r.Payload}"
            : $"caller got ERROR: {response}");
    }
}
