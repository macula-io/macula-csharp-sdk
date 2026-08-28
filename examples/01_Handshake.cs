using Macula.Connection;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>Identity generation, connect, and a clean close -- the smallest possible program that speaks to the mesh.</summary>
public static class Handshake
{
    public static async Task RunAsync()
    {
        // Puzzle-hardened: every station checks this on every CONNECT.
        // Skipping it fails silently -- the QUIC/TLS layer looks healthy,
        // the station just never accepts the HELLO. Sub-millisecond at
        // the default difficulty.
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        Console.WriteLine($"identity node_id = {Convert.ToHexStringLower(identity.NodeId())}");

        await using var session = await Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki);

        Console.WriteLine($"connected -- station node_id = {Convert.ToHexStringLower(session.RemoteInfo.NodeId)}");
        Console.WriteLine($"accepted = {session.RemoteInfo.Accepted}");

        // Session's own DisposeAsync sends GOODBYE and closes -- 'await using' above already handles this.
    }
}
