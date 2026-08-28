namespace Macula.Examples;

/// <summary>
/// The public macula.io demo fleet's Frankfurt node -- throwaway dev
/// infrastructure, no uptime guarantee. `station-de-frankfurt.macula.io`,
/// not the bare `macula.io` hostname: the latter has an A record but no
/// AAAA, and its A record resolves to nothing that's actually listening.
/// </summary>
public static class Station
{
    public const string Host = "station-de-frankfurt.macula.io";
    public const int Port = 4433;
}
