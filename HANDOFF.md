# DMRoute-ng handoff

## Objective

Keep UDP/DMR routing and SDS ingestion allocation-free after startup and warm-up within configured capacities. The current work package replaces the provisional MQTT topics with the canonical `sys`, `routing`, `call`, `sds`, and `diag` tree while retaining the project's `RawMqttClient`.

## Decisions

- Small decentralized deployment defaults: 256 active calls, 8,192 learned local routes, 128 SDS sessions, and 4,096 bytes per SDS message.
- When a bounded table is full, continue routing but do not add new tracking state. Increment a diagnostic counter instead.
- Packet callbacks are synchronous and borrow `ReadOnlySpan<byte>` data only for the duration of the callback.
- IPv4 remains the supported network family.
- MQTT uses only `RawMqttClient`, QoS 0, a hard cut from the legacy topic paths, non-retained event topics, and retained per-entity state topics.
- MQTT event buffering is bounded and keeps the newest events with `DropOldest`. State is rebuilt immediately after a reconnect and every ten seconds by default.
- The hotspot ID in telemetry always comes from the authenticated DMRD packet/repeater state; no runtime hotspot ID is hardcoded.

## Current state

- Baseline before the optimization was 11/11 passing Release tests.
- Static analysis identified per-datagram allocation in `UdpClient.ReceiveAsync`, packet/endpoint copies in router events, allocating `ConcurrentDictionary` enumeration and value updates, `List<byte>`/`ToArray()` SDS reassembly, and array-returning ping builders.
- `AGENTS.md` now records the performance and verification contract.
- Added `Ipv4Endpoint`, changed `IDmrSender`/`Repeater` endpoint storage to the value type, and added span-based `PacketUtils.TryWrite...` APIs while retaining the allocating compatibility wrappers.
- Replaced `DmrServer`'s allocating `UdpClient.ReceiveAsync` path with a dedicated synchronous `Socket.ReceiveFrom` loop using pinned receive storage and reusable receive/send `SocketAddress` instances. ACK/NAK/PONG now use stack buffers.
- Repeater and master registries now expose allocation-free routing snapshots. Local guest tracking uses a preallocated slot table and no longer creates `AddOrUpdate` closures on known-guest packets.
- `MicroSubnetRouter` now uses pre-sized dictionaries, value endpoints, snapshot iteration, source-generated logging, capacity counters, and synchronous borrowed-span frame events.
- Mesh location updates now enter a bounded struct channel from the DMR thread and are serialized/sent on a background loop with reusable packet/address buffers.
- `SdsGateway` now uses a fixed session table and one preallocated reassembly slab. Block ingestion no longer uses `List<byte>`, packet copies, or `ToArray()`; completed text creation remains intentionally allocating.
- `RawMqttClient` now owns a reusable pinned packet buffer, supports reconnecting with a fresh socket, loops over partial TCP sends/receives, and validates packet bounds. `JsonSpanBuilder` now escapes string content.
- Capacity settings are wired through `Program`; the Makefile now defaults to Release/net9.0 and its test target reuses that build. Added protocol, endpoint, JSON and warmed allocation regression tests.
- Added `docs/components/hot-paths.adoc` and updated the router, registry, mesh and MQTT AsciiDoc pages to describe ownership, snapshots, bounded state and the revised guarantees.
- Final review made master endpoint replacement atomic, made mesh worker cancellation observable, and enlarged the MQTT JSON workspace for escaped maximum-size SDS text.
- Split dynamic repeater enrollment from hot-path lookup: only `RPTL` can create a repeater; DMRD, ping, key and config packets now use `TryGetExisting`.
- A real host shutdown exposed a macOS lifecycle issue: disposing the DMR socket did not wake a synchronous native `recvmsg`. A native thread sample confirmed the blocked receive; the loop now uses a 250 ms `Socket.Poll` bound before receiving.
- Replaced the provisional MQTT integration with a bounded `MqttTelemetryQueue` owned by `MqttIntegrationService`. Call and SMS events use value entries; unknown/APRS frames copy into preallocated diagnostic slots. Eviction releases the slot and increments `droppedMqttEvents`.
- Added retained `sys/info`, per-hotspot, per-mesh-peer, guest, away, and per-source active-call state plus non-retained call start/end, SMS, and diagnostic streams. Retained tombstones remove entities that disappear from a registry.
- Call events now carry the packet repeater ID and original UTC ticks. Terminators preserve their receipt tick, so `durationSec` excludes the cleanup delay and is emitted to three decimal places with `endReason=Terminated`; silence expiry uses the last frame and `endReason=Timeout`.
- `RoamingRegistry` is independent of MQTT and exposes bounded span snapshots containing the actual hotspot ID and activity times. Repeater state records login time for MQTT uptime.
- Added allocation-free UTF-8 endpoint formatting and span JSON support for fixed decimals, ISO-8601 UTC timestamps, and hexadecimal payloads. Rewrote the MQTT AsciiDoc contract and updated the active hardware runbook.

## Validation

- Release build succeeds with 0 warnings and 0 errors.
- `make test` passes 31/31 tests in Release mode.
- Warmed allocation tests report 0 bytes for group routing, known ping, unknown DMRD/ping, new state within reserved capacity, a full call table and SDS header ingestion.
- UDP `SocketAddress` round-trip and real loopback send tests pass.
- MQTT fragmented-CONNACK and publish-wire-format integration test passes.
- MQTT queue tests cover `DropOldest`, owned diagnostic copies, slot reuse, and 0-byte warmed enqueue/dequeue. Router hook tests cover 0-byte call/unknown-frame emission and prove that packet hotspot ID `1000042` is preserved.
- A loopback broker test verifies canonical call topics, retained start and empty tombstone frames, `call/event`, the end reason, precise duration, and the actual packet hotspot ID on the wire.
- Every AsciiDoc file under `docs/` and `docs/components/` renders successfully with `asciidoctor`.
- Isolated DMR and Mesh start/stop tests complete within their two-second deadline.
- A complete host smoke test binds both UDP services, handles an unavailable MQTT broker, and exits cleanly with code 0 after `Ctrl+C`.

## Hardware validation

- Added `docs/hardware-test-plan.adoc` for a Zone 100 smoke test with radios `10001` (local), `10101` (guest), and one Homebrew/MMDVM hotspot.
- The plan covers login/authentication, RPTC, ping/pong, group and private calls, guest tracking, disconnect/reconnect, and bidirectional SDS/SMS.
- The real hardware run from 2026-09-12 is documented in `docs/hardware-test-results-2026-09-12.adoc`. Login/keepalive, local and guest group calls, private routing in both directions, MQTT events, timeout/reconnect, and confirmed SDS `10001 -> 10101` passed.
- Confirmed SDS `10101 -> 10001` reached and returned its CSBK/data header, but the Retevis radio sent no subsequent rate-3/4 blocks. Unconfirmed group SDS from that same radio decoded correctly, localizing the failure to this confirmed handshake rather than DMRoute-ng reassembly.
- The capture contains 439 inbound and 155 outbound DMRD packets. Private voice frames are paired in both directions; group traffic is not echoed to its source hotspot.
- Group SDS to TG 9 arrived with the Homebrew unit-call bit set. The decoder published it, but the router treated destination 9 as a Zone 0 unit ID. Multi-hotspot group-SDS classification/routing remains an explicit follow-up.
- With one RF endpoint, received audio is not proof of master routing. Private-call return routing is verified from outbound DMRD packets in `/tmp/dmroute-hardware.pcap`; group DMRD must not be echoed to its source hotspot.
- Evidence remains in `/tmp/dmroute-hardware.log`, `/tmp/dmroute-mqtt.log`, and `/tmp/dmroute-hardware.pcap`; these runtime artifacts are intentionally not committed.
- The Makefile `run` target now names the executable project explicitly and reuses the existing Release build, so it works from the solution root with environment-based configuration.
- The historical MQTT paths in the 2026-09-12 result remain marked as the topics used by that test run; the current runbook uses the new canonical paths.

## Known limits

- Completed SMS strings and MQTT connection work may allocate by design.
- MQTT credentials and TLS are not implemented. `sds/gps` is reserved; APRS/raw position traffic currently appears under `diag/unknown_frame` with data type `0x03`.
- A QoS-0 event already removed from the channel can be lost if the TCP publish fails. Retained registry state is reconstructed after reconnect; diagnostic frames longer than `Mqtt:MaxDiagnosticFrameBytes` are truncated.
- Topology snapshot replacement and initial repeater enrollment allocate outside the steady packet path.
- The hard allocation guarantee is covered for the listed synchronous processing paths; it does not attempt to include exception construction or enabled logging sinks.

## Precise next step

Review PR #7 and merge it after its GitHub checks pass; issues #3 and #6 close through the PR description. Then profile and optimize `RawMqttClient` itself as a separate task; credentials/TLS and a decoded `sds/gps` publisher remain later feature work.
