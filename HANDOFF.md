# DMRoute-ng handoff

## Active work: configuration wizard (#8)

- Branch `feat/config-wizard` was created from the current working tree.
- Implemented an AOT-safe, dependency-free full-screen setup wizard with basic and advanced settings, masked PSKs, validation, atomic JSON writes and cancellation without changes.
- Added `--setup`/`-Setup` and `--config`/`-Config`; every existing configuration command-line argument is forwarded unchanged and applied last so it has highest priority.
- Centralized explicit scalar configuration reads and validation in typed immutable settings shared by startup and MQTT integration. JSON output uses source-generated metadata and reflection serialization is disabled.
- Added configuration, precedence, JSON and scripted-terminal tests. `make test` passes 60/60 tests and a local `osx-arm64` NativeAOT publish succeeds; the existing release workflow remains responsible for `linux-x64` and `win-x64` publishes.
- Corrected the 2026-09-12 hardware report: the capture includes an initial unit-addressed SDS attempt to ID 9 and a later correctly flagged group SDS, so it does not demonstrate a router classification defect.
- GitHub issue #8 was rewritten with the implemented interface and validation evidence and renamed to `Config-Wizard (TUI)`. It remains open and will be closed by the PR on merge.

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
- Added allocation-free NMEA RMC parsing for the AnyTone APRS data stream and direct UTF-16LE parsing for manually sent AnyTone GPS information. Both paths emit `DmrLocationEvent` with the actual packet hotspot ID and master receive timestamp.
- Removed the provisional CSBK `0x03` APRS hook: the CSBK only announces the transfer, while the position data is reassembled from the following `0x06`/`0x07` frames.
- Added non-retained `dmroute/{zoneId}/sds/gps` publishing through `RawMqttClient`. MQTT location privacy defaults to a deterministic 10 km grid; exact coordinates can be enabled explicitly. Recognized GPS SMS text is replaced with `[GPS position redacted]` while privacy is active, including malformed `Template:` messages.
- Added `docs/components/location-telemetry.adoc` with the input formats, data flow, raster equations, configuration, payload, protection properties, and limits. The hardware runbook now includes the H08 location/privacy acceptance test.

## Validation

- Release build succeeds with 0 warnings and 0 errors.
- `make test` passes 48/48 tests in Release mode.
- Warmed allocation tests report 0 bytes for group routing, known ping, unknown DMRD/ping, new state within reserved capacity, a full call table and SDS header ingestion.
- UDP `SocketAddress` round-trip and real loopback send tests pass.
- MQTT fragmented-CONNACK and publish-wire-format integration test passes.
- MQTT queue tests cover `DropOldest`, owned diagnostic copies, slot reuse, and 0-byte warmed enqueue/dequeue. Router hook tests cover 0-byte call/unknown-frame emission and prove that packet hotspot ID `1000042` is preserved.
- A loopback broker test verifies canonical call topics, retained start and empty tombstone frames, `call/event`, the end reason, precise duration, and the actual packet hotspot ID on the wire.
- Every AsciiDoc file under `docs/` and `docs/components/` renders successfully with `asciidoctor`.
- Isolated DMR and Mesh start/stop tests complete within their two-second deadline.
- A complete host smoke test binds both UDP services, handles an unavailable MQTT broker, and exits cleanly with code 0 after `Ctrl+C`.
- Captured DMRD frames reassemble and decode 100/100 times with 0 managed bytes allocated after warm-up. Enqueue/dequeue of 1,000 location value events also allocates 0 bytes.
- Location tests cover the captured live RMC report, `GPRMC`/`GNRMC`, hemispheres, invalid fixes and checksums, AnyTone UTF-16LE GPS text, malformed-message redaction, and stable grid behavior including the dateline and polar region.
- Raw MQTT loopback tests verify safe privacy defaults, explicit enable/disable, coordinate rastering, exact opt-out values, actual hotspot ID, null optional values, non-retained publication, SMS redaction, and rejection of invalid grid sizes.

## Hardware validation

- Added `docs/hardware-test-plan.adoc` for a Zone 100 smoke test with radios `10001` (local), `10101` (guest), and one Homebrew/MMDVM hotspot.
- The plan covers login/authentication, RPTC, ping/pong, group and private calls, guest tracking, disconnect/reconnect, and bidirectional SDS/SMS.
- The real hardware run from 2026-09-12 is documented in `docs/hardware-test-results-2026-09-12.adoc`. Login/keepalive, local and guest group calls, private routing in both directions, MQTT events, timeout/reconnect, and confirmed SDS `10001 -> 10101` passed.
- Confirmed SDS `10101 -> 10001` reached and returned its CSBK/data header, but the Retevis radio sent no subsequent rate-3/4 blocks. Unconfirmed group SDS from that same radio decoded correctly, localizing the failure to this confirmed handshake rather than DMRoute-ng reassembly.
- The capture contains 439 inbound and 155 outbound DMRD packets. Private voice frames are paired in both directions; group traffic is not echoed to its source hotspot.
- The SDS/TG9 capture was re-evaluated: early attempts were unit-addressed to ID 9, while the final successful sequence carried the group bit. The hardware report now corrects the former router-defect interpretation; a real multi-hotspot distribution test remains future validation.
- With one RF endpoint, received audio is not proof of master routing. Private-call return routing is verified from outbound DMRD packets in `/tmp/dmroute-hardware.pcap`; group DMRD must not be echoed to its source hotspot.
- Evidence remains in `/tmp/dmroute-hardware.log`, `/tmp/dmroute-mqtt.log`, and `/tmp/dmroute-hardware.pcap`; these runtime artifacts are intentionally not committed.
- The Makefile `run` target now names the executable project explicitly and reuses the existing Release build, so it works from the solution root with environment-based configuration.
- The historical MQTT paths in the 2026-09-12 result remain marked as the topics used by that test run; the current runbook uses the new canonical paths.
- Two AnyTone location captures were analyzed: the configured fixed beacon and a live satellite fix. A manually sent GPS information message supplied coordinates, speed, and altitude as UTF-16LE TMS text. Exact local coordinates are deliberately omitted from tracked files.
- The private location captures remain in `/tmp/dmroute-aprs-real.pcap` and `/tmp/dmroute-aprs-gps.pcap` and are intentionally excluded from Git.
- H08 passed on 2026-09-13 against a local MQTT probe: automatic `NMEA_RMC` and manual `ANYTONE_GPS_TEXT` events carried hotspot ID `1000001`, were non-retained, used the same 10 km grid cell, and stayed within 10 km of the decoded fixes. The paired manual SMS contained only `[GPS position redacted]`; an automated scan found no exact manual coordinate in MQTT and `droppedMqttEvents` remained `0`.
- The privacy-safe MQTT evidence is `/tmp/dmroute-location-mqtt.log`; `docs/hardware-test-results-2026-09-13-location.adoc` records the result without exact local coordinates.

## Known limits

- Completed SMS strings and MQTT connection work may allocate by design.
- MQTT credentials and TLS are not implemented.
- A QoS-0 event already removed from the channel can be lost if the TCP publish fails. Retained registry state is reconstructed after reconnect; diagnostic frames longer than `Mqtt:MaxDiagnosticFrameBytes` are truncated.
- Topology snapshot replacement and initial repeater enrollment allocate outside the steady packet path.
- The hard allocation guarantee is covered for the listed synchronous processing paths; it does not attempt to include exception construction or enabled logging sinks.
- The privacy raster applies at the MQTT boundary only. Exact GPS text remains visible to local logging, and IDs, timestamps, speed, course, altitude, and coarse movement remain visible on MQTT. The mechanism reduces precision but is not anonymization or cryptographic location protection.
- An RMC `A` status proves only what the radio reports. The tested AnyTone also labels its configured fixed beacon as valid, so the radio must use `APRS -> Upload Beacon -> GPS Beacon` for a satellite-derived position.

## Precise next step

Review and merge the PR from `feat/config-wizard`; its `Closes #8` reference will close the issue. The existing release workflow will perform the `linux-x64` and `win-x64` NativeAOT publishes after the test workflow succeeds on the default branch.
