using Macula.Connection;
using Macula.Frame;
using Macula.Identity;
using Macula.Streaming;

namespace Macula.Examples;

/// <summary>
/// Streaming RPC, provider role: one session advertises a procedure and
/// accepts inbound STREAM_OPENs for it; a second session dials in and
/// pulls data from it (server_stream mode: the provider pushes chunks).
/// Two <see cref="Session"/>s in one process, same as the unary-RPC
/// provider example.
/// </summary>
public static class StreamProvider
{
    public static async Task RunAsync()
    {
        var providerIdentity = KeyPair.GenerateWithDefaultPuzzle();
        var callerIdentity = KeyPair.GenerateWithDefaultPuzzle();

        await using var providerSession = await Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki);
        await using var callerSession = await Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki);

        var realm = new byte[32];
        var procedure = $"macula_csharp_sdk.examples.stream_provider.{Guid.NewGuid():N}";

        await providerSession.AdvertiseAsync(new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId() });
        Console.WriteLine($"advertised {procedure}");
        await Task.Delay(500);

        var acceptTask = StreamHandle.AcceptAsync(providerSession, TimeSpan.FromSeconds(10));

        var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
        var callerHandle = await StreamHandle.OpenAsync(callerSession, procedure, realm, StreamMode.ServerStream, Value.Null, deadlineMs, callerIdentity);

        var (providerHandle, openInfo) = await acceptTask;
        Console.WriteLine($"provider accepted stream_open for procedure={openInfo.Procedure} mode={openInfo.Mode}");

        await providerHandle.SendDataAsync(StreamEncoding.Raw, Value.Bytes("hello from the provider"u8.ToArray()), providerIdentity);
        await providerHandle.CloseSendAsync(providerIdentity);

        var item = await callerHandle.RecvAsync(TimeSpan.FromSeconds(5));
        if (item is StreamItem.Data data)
        {
            Console.WriteLine($"caller received: {System.Text.Encoding.UTF8.GetString(data.Body.AsBytes())}");
        }

        var eof = await callerHandle.RecvAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine($"caller saw {(eof == StreamItem.Eof ? "end-of-stream" : "something unexpected")}");
    }
}
