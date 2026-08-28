# macula-csharp-sdk

[![CI](https://img.shields.io/github/actions/workflow/status/macula-io/macula-csharp-sdk/ci.yml?branch=main&label=CI)](https://github.com/macula-io/macula-csharp-sdk/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0%20OR%20MIT-blue.svg)](#license)
[![.NET](https://img.shields.io/badge/.NET-10.0%2B-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![no FFI](https://img.shields.io/badge/FFI-none-success.svg)](#why-native-not-a-binding)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-support-yellow.svg)](https://buymeacoffee.com/rlefever)

<p align="center">
  <strong>Native C# port of the Macula SDK wire protocol</strong>
</p>

---

> **Status, 2026-08-28:** the FULL wire protocol is built and
> **live-verified against the production station fleet**
> (`station-de-frankfurt.macula.io`) — handshake, unary RPC (both caller
> AND provider), PubSub, content transfer, and streaming RPC, every
> primitive in both caller and provider roles where the protocol has
> one. The frame layer is cross-checked byte-for-byte — including the
> Ed25519 signature itself — against
> [`macula-rust-sdk`](https://github.com/macula-io/macula-rust-sdk)'s own
> fixed reference vectors: 15 golden frames, all matching. See
> [Status](#status) for the full picture.

## What is this?

A native C# implementation of the client half of Macula's wire protocol —
the same protocol [`macula-io/macula`](https://github.com/macula-io/macula)
(the Erlang/OTP SDK) speaks, and the same protocol
[`macula-go-sdk`](https://github.com/macula-io/macula-go-sdk) and
[`macula-rust-sdk`](https://github.com/macula-io/macula-rust-sdk) already
port. Macula is a federated mesh for sovereign, end-to-end-encrypted
application networks; a **station** is the relay/DHT node, and this
library is what a **leaf** — anything that isn't itself a station — uses
to join it.

## Why native, not a binding

Unlike [`macula-php-sdk`](https://github.com/macula-io/macula-php-sdk)
(a thin FFI binding over the Go SDK's compiled C ABI, the right call for
PHP), this is a full independent port: its own deterministic CBOR codec,
its own Ed25519/S/Kademlia identity layer, its own frame construction —
built the same way the Go and Rust ports were, not wrapped around either
of them. Two things make that practical for C# where it wasn't for PHP:
.NET has shipped a real QUIC client (`System.Net.Quic`) since .NET 7, and
`System.Formats.Cbor` already covers general CBOR mechanics (though not
this protocol's specific canonicalization rules — see
[The CBOR codec is hand-rolled on purpose](#the-cbor-codec-is-hand-rolled-on-purpose)).
No cgo, no native `.so`/`.dll` shipped by this repo, no P/Invoke into
anything macula-specific — `dotnet add package Macula.Client` is the
whole install.

The one native dependency that's unavoidable regardless of approach is
QUIC itself: `System.Net.Quic` needs `libmsquic`, which Microsoft doesn't
publish for Linux on NuGet (only via the `packages.microsoft.com` apt/dnf
repo). This library depends on the community
[`Unofficial.MsQuic`](https://www.nuget.org/packages/Unofficial.MsQuic)
package instead, which ships `libmsquic` for linux-x64/linux-arm64/win-x64
as ordinary NuGet runtime assets — so `dotnet add package` really is
still the whole install, no system package manager required.

## Why a fourth implementation matters

Four independent implementations (Erlang reference, Rust, Go, now C#)
producing bit-identical wire bytes for the same input is a much stronger
correctness claim than any one of them alone. This repo's own
`FrameGoldenVectorTests` builds the exact same 15 signed frames
`macula-rust-sdk`'s own differential-vector tests build — same identity,
same fixed `frame_id`/`sent_at_ms`/`call_id`/`stream_id` — and asserts
the Ed25519 signature, not just the frame shape, matches byte for byte
against vectors originally captured from a real `rebar3 shell` against
`macula-io/macula` itself. If this port's canonical CBOR encoder or
signing domain diverged from the other three anywhere, this would fail;
it doesn't.

## Features

| Primitive | Caller | Provider | Notes |
|---|---|---|---|
| Handshake (CONNECT/HELLO) | ✅ | — | Ed25519 identity, S/Kademlia puzzle-hardened; HELLO signature verified; live-verified |
| Deterministic CBOR codec | ✅ | — | Hand-rolled — see [Codec](#the-cbor-codec-is-hand-rolled-on-purpose) |
| Unary RPC (CALL/RESULT/ERROR) | ✅ | ✅ | `Session.ServeOneCallAsync`, BOLT#4 error mapping, live-verified |
| PubSub (PUBLISH/SUBSCRIBE/EVENT) | ✅ | ✅ | A subscriber gets its own publish, verified live |
| Content transfer (single-block + chunked) | ✅ | — | Content-addressed, BLAKE3/SHA-256, Merkle-verified |
| Streaming RPC (STREAM_OPEN/DATA/END/REPLY) | ✅ | ✅ | Both roles live-verified against the real fleet |
| RPC advertise/unadvertise | ✅ | — | |
| Pubkey-pinned trust | ✅ | — | `Trust.Pin(nodeId)` — Ed25519 SPKI match, no CA chain needed |

## Structure

```
src/Macula/
  Value.cs                 The wire protocol's closed value model
  Cbor/CborCodec.cs        Deterministic CBOR encode/decode
  Identity/                Ed25519 keypair, S/Kademlia puzzle
  Bolt4/                   17-entry BOLT#4 error taxonomy
  Frame/                   Envelope, signing, wire codec, every frame type
  Connection/               QUIC transport, handshake, Session (RPC/PubSub/serve)
  Content/                  Manifest (chunking/Merkle), put/get
  Streaming/                StreamHandle -- caller and provider roles
examples/                  One example per primitive (see below)
tests/Macula.Tests/         Offline unit tests + live station tests
```

## Quick start

Also lives as a runnable example — `dotnet run --project examples -- 01`:

```csharp
using Macula.Connection;
using Macula.Identity;

// Puzzle-hardened identity -- required. An unhardened identity fails the
// handshake silently (QUIC/TLS looks healthy, HELLO never accepts).
var identity = KeyPair.GenerateWithDefaultPuzzle();

await using var session = await Session.ConnectAsync(
    "station-de-frankfurt.macula.io", 4433, identity, Trust.UseWebPki);

Console.WriteLine($"connected -- station node_id = {Convert.ToHexStringLower(session.RemoteInfo.NodeId)}");
// session's own DisposeAsync sends GOODBYE and closes.
```

## Examples

| # | Run | What it shows |
|---|---|---|
| 01 | `dotnet run --project examples -- 01` | Identity + connect + close |
| 02 | `dotnet run --project examples -- 02` | Unary RPC caller (CALL/RESULT/ERROR) |
| 03 | `dotnet run --project examples -- 03` | PubSub (subscribe, publish, receive the EVENT) |
| 04 | `dotnet run --project examples -- 04` | Content transfer, single-block and chunked |
| 05 | `dotnet run --project examples -- 05` | Streaming RPC, caller role |
| 06 | `dotnet run --project examples -- 06` | Unary RPC, provider role (two `Session`s, one process) |
| 07 | `dotnet run --project examples -- 07` | Streaming RPC, provider role (two `Session`s, one process) |

Examples 06 and 07 run both roles in **one process, two `Session`s** —
there's no cgo/fork hazard here the way there was in
[`macula-php-sdk`](https://github.com/macula-io/macula-php-sdk)'s
FFI-over-Go binding, so a provider and a caller can just be two `Task`s
in the same `async` method.

## Testing

```bash
dotnet test --filter "Category!=Live"   # offline: pure logic + 15 golden byte-exact frame vectors
dotnet test --filter "Category=Live"    # dials the real production fleet -- see below
```

The offline suite (run in CI on every push) never touches the network:
CBOR codec edge cases, Ed25519/puzzle vectors captured from a real
`rebar3 shell`, and all 15 golden frame vectors. The live suite dials
`station-de-frankfurt.macula.io` — throwaway dev infrastructure with no
uptime guarantee, so it's excluded from CI and only run manually.

**Running the live suite locally on a distro without `libmsquic`'s
required OpenSSL version:** `Unofficial.MsQuic`'s Linux build links
against OpenSSL 1.1, which some distros (Arch among them) no longer ship.
If `dotnet test --filter Category=Live` fails with `Unable to load MsQuic
library version '2'`, check `ldd` on the restored
`runtimes/linux-x64/native/libmsquic.so` for `libcrypto.so.1.1 => not
found` — if so, either install an OpenSSL 1.1 compatibility package, or
substitute any locally-built `libmsquic.so` compiled against your
system's actual OpenSSL (msquic itself has no other requirements).

## The CBOR codec is hand-rolled on purpose

Every frame's Ed25519 signature is computed over the *canonical* CBOR
bytes of the frame, and macula's canonicalization rules diverge from
RFC 8949's own recommendations in one deliberate way: floats always
encode as full binary64 (`0xFB` + 8 bytes), never the shortest
round-tripping width the RFC prefers. `System.Formats.Cbor` (the BCL's
own CBOR writer) implements the RFC's canonical mode, not this one — so
using it here would silently produce non-verifying signatures against a
real station. `Cbor/CborCodec.cs` is a direct, from-scratch
transcription of the same ~200-line deterministic algorithm the Erlang
reference (`macula_cbor_nif`'s `deterministic.rs`) and the Rust/Go ports
already use: minimal-length integers, map keys sorted by their own
*encoded* bytes (not their logical value), `procedure`/`topic`/`detail`
as raw byte strings even though most other string-ish fields are text,
and floats always full-width.

## Real findings from building this against the live fleet

Two things worth knowing if you're integrating against the real station,
neither obvious from the wire-protocol spec alone:

- **`System.Net.Quic` accepts zero inbound streams by default.** Unlike
  `quinn` (Rust's QUIC crate), a `QuicConnection` that doesn't set
  `MaxInboundBidirectionalStreams`/`MaxInboundUnidirectionalStreams`
  throws `InvalidOperationException` the moment anything calls
  `AcceptInboundStreamAsync` — including the station routing an inbound
  STREAM_OPEN to an advertised procedure. `Session.ConnectAsync` sets
  both to 100 unconditionally, since the decision to advertise happens
  after the connection already exists.
- **The station periodically sends unprompted `advertise` frames for its
  own built-in `_content.*` procedures over every connected client's
  control stream** — observed directly while testing the PubSub example,
  not documented anywhere in the wire-protocol spec. `Session.RecvEventAsync`
  errors on the first non-EVENT frame rather than silently skipping it
  (matching the Go/Rust/PHP SDKs — a caller waiting specifically for a
  pubsub delivery has no reason to expect anything else), so a real
  caller sharing a control stream between PubSub and anything else should
  loop past frames it doesn't recognize rather than call it exactly once.
  See `examples/03_PublishSubscribe.cs`'s own `RecvEventSkippingOtherTrafficAsync`.

## Status

Every application primitive the wire protocol defines is built, in both
roles where the protocol has one, and live-verified against
`station-de-frankfurt.macula.io`:

- Transport + handshake (CONNECT/HELLO, Ed25519 identity, S/Kademlia
  puzzle, WebPki and pubkey-pinned trust)
- Unary RPC, caller and provider (`Session.CallAsync` /
  `Session.ServeOneCallAsync`)
- PubSub, caller and provider (a subscriber does receive its own publish)
- Content transfer, single-block and chunked (sequential v1, matching
  the Go/Rust SDKs — multi-lane parallelism is a throughput optimization
  addable later with zero wire change)
- Streaming RPC, caller and provider (`StreamHandle.OpenAsync` /
  `StreamHandle.AcceptAsync`)

Not built, matching every sibling SDK's own documented v1 scope: DHT,
HyParView, Plumtree gossip primitives — these are station-to-station
overlay concerns, explicitly out of scope for a leaf client by design,
not an unfinished gap.

## License

Dual-licensed under Apache-2.0 or MIT, at your option — see
[LICENSE-APACHE](LICENSE-APACHE) and [LICENSE-MIT](LICENSE-MIT).
