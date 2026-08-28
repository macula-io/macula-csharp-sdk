module Station

/// The public macula.io demo fleet's Frankfurt node -- throwaway dev
/// infrastructure, no uptime guarantee. `station-de-frankfurt.macula.io`,
/// not the bare `macula.io` hostname: the latter has an A record but no
/// AAAA, and its A record resolves to nothing that's actually listening.
[<Literal>]
let Host = "station-de-frankfurt.macula.io"

[<Literal>]
let Port = 4433
