# DMRoute-ng handoff

## Active work: Radio Check capture preparation for #9

- PR #20 was merged into `main` as `acc4860` and established #9 as the next work package.
- Work is on `feat/issue-9-radio-check`, created from the updated `origin/main`.
- The German bidirectional AnyTone/Retevis capture procedure is prepared; the physical run is the next gate.
- Follow-up issue #21 tracks master-initiated Radio Checks and later roaming expiry integration.

## Objective

Capture successful and unanswered Radio Checks from both radio families, derive the verified CSBK format and timings, and turn #9 into an implementation-ready contract.

## Decisions

- The first #9 implementation will passively recognize and route Radio Check requests and responses. Master-originated checks are deferred to #21.
- MQTT and source-generated logs will represent one logical transaction with `Requested`, then exactly one `Answered` or `TimedOut` result.
- Terminal results will expose `attempts`, `retries = attempts - 1`, and duration so operators can spot links that need repeated RF transmissions.
- Only matching request CSBKs received from the hotspot count as attempts. Master return traffic, capture duplicates, preambles and unrelated CSBKs do not increment retries.
- One physical hotspot is sufficient for packet-format analysis and proves UDP return routing. It cannot isolate the RF path between two radios on the same frequency.
- No production parser will be written from assumed fields. The AnyTone/Retevis online and offline captures are the implementation gate.
- Issue #9 is the next work package because its bounded protocol scope can be captured with both available radio families and then automated with the #11 test harness.
- Issue #10 follows #9 and delivers the larger outbound SDS encoder, API and delivery state model.
- Issue #15 follows both protocol efforts and turns their verified packet flows into semantic emulator scenarios.
- Issue #16 follows #15 so multi-master tests can use the mature scenario API. Issue #14 follows after that API stabilizes; hardware-coupled #17 remains last.
- `tools/DMRoute_ng.Emulator` is a .NET 9 core library with no reference to the DMRoute-ng server assembly or xUnit. A later CLI/TUI will consume its public API.
- `VirtualHotspot` owns the UDP session and models login, configuration, keepalive, disconnect, bounded DMRD capture and explicit protocol states. `VirtualRadio` replays owned `RadioScenario` frames with relative timing.
- The first version replays sanitized captured DMRD frames. Semantic voice, CSBK and SDS generation is deferred to #15.
- Production binding remains `0.0.0.0:62031`; internal test hooks allow port `0`, await socket readiness and trigger the existing 45-second repeater liveness sweep deterministically.
- Small decentralized deployment defaults: 256 active calls, 8,192 learned local routes, 128 SDS sessions, and 4,096 bytes per SDS message.
- When a bounded table is full, continue routing but do not add new tracking state. Increment a diagnostic counter instead.
- Packet callbacks are synchronous and borrow `ReadOnlySpan<byte>` data only for the duration of the callback.
- IPv4 remains the supported network family.
- MQTT uses only `RawMqttClient`, QoS 0, a hard cut from the legacy topic paths, non-retained event topics, and retained per-entity state topics.
- MQTT event buffering is bounded and keeps the newest events with `DropOldest`. State is rebuilt immediately after a reconnect and every ten seconds by default.
- The hotspot ID in telemetry always comes from the authenticated DMRD packet/repeater state; no runtime hotspot ID is hardcoded.

## Current state

- `docs/radio-check-capture-plan.adoc` defines setup, route priming, twelve recorded runs, observation fields, retry counting and evidence handling.
- Existing captures contain voice, APRS and SDS traffic but no deliberately triggered Radio Check.
- `MicroSubnetRouter` already routes private DMRD packets byte-for-byte. It does not decode CSBK content, and a low-nibble `0x03` without the data-sync bit is a Voice-C burst rather than CSBK.
- Local raw capture formats and the `captures/` directory are excluded through `.gitignore`; evidence remains under `/tmp`.
- Homebrew packet writers cover RPTL, RPTK, the complete 302-byte RPTC layout, RPTPING and RPTCL; response parsers cover RPTACK, MSTPONG and MSTNAK.
- The emulator rejects non-IPv4 endpoints, invalid capacities/timeouts, non-ASCII Homebrew fields, mismatched radio IDs and DMRD frames for another hotspot.
- Integration tests log in two concurrent hotspots on ephemeral loopback ports, persist RPTC metadata, exercise manual and periodic ping, forward a captured group-data frame byte-for-byte and prove that the source receives no echo.
- Negative integration tests cover a wrong PSK, a foreign-zone ID, liveness timeout, same-endpoint soft reconnect, RPTCL and full reauthentication.
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
- `make test` passes 68/68 tests in Release mode.
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

- The emulator currently has only a programmatic API and recorded-frame replay; interactive use and semantic frame generation are tracked in #14 and #15.
- UDP is intentionally IPv4-only, matching the server. Emulator receive queues are bounded and fail visibly on overflow.
- Completed SMS strings and MQTT connection work may allocate by design.
- MQTT credentials and TLS are not implemented.
- A QoS-0 event already removed from the channel can be lost if the TCP publish fails. Retained registry state is reconstructed after reconnect; diagnostic frames longer than `Mqtt:MaxDiagnosticFrameBytes` are truncated.
- Topology snapshot replacement and initial repeater enrollment allocate outside the steady packet path.
- The hard allocation guarantee is covered for the listed synchronous processing paths; it does not attempt to include exception construction or enabled logging sinks.
- The privacy raster applies at the MQTT boundary only. Exact GPS text remains visible to local logging, and IDs, timestamps, speed, course, altitude, and coarse movement remain visible on MQTT. The mechanism reduces precision but is not anonymization or cryptographic location protection.
- An RMC `A` status proves only what the radio reports. The tested AnyTone also labels its configured fixed beacon as valid, so the radio must use `APRS -> Upload Beacon -> GPS Beacon` for a satellite-derived position.

## Precise next step

Run `docs/radio-check-capture-plan.adoc` with the AnyTone and Retevis, then provide the three `/tmp/dmroute-radio-check*` artifacts and the twelve timestamped device results for protocol analysis.
