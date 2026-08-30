using Macula.Connection;
using Macula.Dht;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>
/// Direct-dial: a provider advertises via <see cref="DirectDial.AdvertiseDirectAsync"/>
/// (publishing a signed procedure_advertisement DHT record AND the plain
/// ADVERTISE), a separate session resolves that record and dials the
/// provider's station directly -- reaching it without depending on
/// ordinary advertise-gossip having propagated a route between whichever
/// two stations happen to be involved. Two <see cref="Session"/>s, two
/// identities (this fleet kicks whichever connection reuses an identity
/// second).
/// </summary>
public static class DirectDialExample
{
    public static async Task RunAsync()
    {
        var providerIdentity = KeyPair.GenerateWithDefaultPuzzle();
        var callerIdentity = KeyPair.GenerateWithDefaultPuzzle();

        await using var providerSession = await Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki);
        await using var resolverSession = await Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki);

        var realm = new byte[32];
        var procedure = $"macula_dotnet_sdk.examples.direct_dial.{Guid.NewGuid():N}";

        await DirectDial.AdvertiseDirectAsync(providerSession, providerIdentity, realm, procedure, TimeSpan.FromHours(1));
        Console.WriteLine($"provider advertised {procedure} (plain + direct)");

        CallLookup lookup = (_, proc) =>
        {
            if (proc != procedure)
            {
                return null;
            }
            return payload => Task.FromResult(payload);
        };
        var serveTask = providerSession.ServeOneCallAsync(lookup, TimeSpan.FromSeconds(15));

        var resolved = await DirectDial.ResolveAsync(resolverSession, realm, procedure);
        Console.WriteLine($"resolved {procedure} -> station={Convert.ToHexStringLower(resolved.Station)} host={resolved.Host} port={resolved.Port}");

        var response = await DirectDial.CallAsync(resolverSession, callerIdentity, realm, procedure, Value.Text("hello direct-dial"), TimeSpan.FromSeconds(10));
        await serveTask;

        Console.WriteLine(response is CallResponse.Result r
            ? $"caller got real RESULT through direct-dial: {r.Payload}"
            : $"caller got ERROR: {response}");
    }
}
