# DMRoute-ng handoff

## Active work: Nix-Flake für Entwicklung und Release

- Branch: `feat/nix-flake-release`, gestapelt auf
  `feat/blip25-voice-encoder`/Draft-PR #29.
- `flake.lock` pinnt nixpkgs auf Commit
  `d3b13ad75cc492690728f9307e443dec9ef9a20e` vom 24. Oktober 2026.
  `nix/server-deps.json` enthält die externen NuGet-Versionen und -Hashes. Die
  Linux-x64-Runtimepacks kommen aus dem gepinnten .NET-SDK; der Playground hat
  keine weiteren NuGet-Abhängigkeiten. Der Rust-Encoder verwendet weiterhin
  seine eigene `Cargo.lock`.
- Die Flake bietet auf Apple Silicon und Linux `openDmr`, `documentation`,
  `blip25-encoder` und eine Dev-Shell. `server`, `playground` und
  `releaseBundle` sind bewusst nur auf `x86_64-linux` sichtbar.
- `releaseBundle` behält die öffentlichen Artefaktnamen bei: Serververzeichnis,
  Playground-Tarball mit OpenDMR-Quellen und Hinweisen sowie beide PDF-Bücher.
  Der experimentelle blip25-Encoder bleibt bis VC10 außerhalb des Bundles.
- Der Release-Workflow baut das Linux-Bundle mit Nix und veröffentlicht alle
  Linux-/Doku-Dateien in einem Job. Windows x64 bleibt beim bisherigen
  NativeAOT-Publish. Der Cleanup wartet auf beide Plattformjobs.
- Der Testworkflow baut `releaseBundle` auf Linux. Dadurch kann ein Release nur
  nach einem erfolgreichen reproduzierbaren Linux-Build ausgelöst werden.
- Lokale Validierung: `nix flake check --all-systems --no-build` evaluiert alle
  Linux- und macOS-Ausgaben. `nix flake check` baut auf Apple Silicon OpenDMR
  samt Regressionstest, beide Dokumentationsbücher und den Rust-Encoder mit
  2/2 Unit-Tests. Linux NativeAOT kann auf diesem Apple-Silicon-Host nicht
  ausgeführt werden und wird durch den neuen GitHub-Job geprüft.
- Der erste Linux-Lauf zeigte doppelte Runtimepacks, weil der erzeugte
  NuGet-Stand dieselben Pakete wie das gepinnte Nix-.NET-SDK enthielt. Die drei
  Runtimeeinträge wurden aus beiden NuGet-Dateien entfernt; das SDK liefert sie
  bereits mit identischem Hash.
- Der zweite Linux-Lauf baute Server-NativeAOT, Playground, OpenDMR und beide
  Bücher erfolgreich. Nur das Bundle scheiterte, weil `cp -a` den
  schreibgeschützten Modus des Playground-Store-Verzeichnisses übernahm. Das
  neue Bundle macht ausschließlich seine eigene Kopie vor den Zusatzdateien
  beschreibbar.
- Der dritte PR-Lauf ist vollständig grün. `nix-release` baute das komplette
  Linux-x64-Bundle einschließlich NativeAOT, Playground, OpenDMR-Test und PDFs
  in 71 Sekunden; .NET-Tests, Rust-Encoder, Dokumentation und beide bisherigen
  Playground-Matrizen bestanden ebenfalls.
- Draft-PR #30 enthält den Endstand und schließt Issue #23 erst beim Merge.
  Präziser nächster Schritt ist der Wechsel zurück auf
  `feat/blip25-voice-encoder` und der verdeckte VC10-A/B-Vergleich auf TG 1.

## Active work: experimenteller blip25-Encoder

- Branch: `feat/blip25-voice-encoder`, gestapelt auf
  `feat/opendmr-playground-voice`/Draft-PR #28.
- `tools/dmroute-blip25-encoder` ist ein eigenständiges Rust-Programm mit den
  Befehlen `info` und `encode`. Die Abhängigkeit `blip25-vocoder` ist per
  `Cargo.lock` exakt auf 1.0.0 festgelegt. Das Programm packt
  `C0(24)|C1(23)|C2(11)|C3(14)` als kanonisches 9-Byte-Codewort und gleicht die
  interne Ein-Frame-Verzögerung aus, sodass N PCM-Frames genau N AMBE-Frames
  ergeben.
- Der Playground akzeptiert zusätzlich `--voice-encoder` und
  `Codec:EncoderExecutable`. Die Encoderpriorität lautet: neues CLI-Argument,
  bestehendes `--open-dmr`, neuer Profilwert, bestehender OpenDMR-Profilwert,
  bisherige OpenDMR-Suche. Der Decoder verwendet unverändert ausschließlich
  die OpenDMR-Reihenfolge. Bestehende Aufrufe bleiben gültig und explizite
  Kommandozeilenargumente haben Vorrang.
- `voice info` zeigt Decoder und Encoder getrennt. Der lokale, ignorierte
  `dmroute-playground.json` verweist auf den Release-Build des Rust-Encoders und
  auf OpenDMR als Decoder.
- Lizenz- und Herkunftshinweise nennen ausdrücklich das Upstream-Reverse-
  Engineering, Commit `d1762813b7142c021f4105c832bf7f060d1249fa`, MIT und den
  Upstream-Patenthinsweis. DMRoute-ng behauptet keine Clean-Room-Entwicklung.
  Der Encoder wird vorerst nicht in reguläre Releases aufgenommen.
- `VC10` beschreibt drei verdeckte A/B-Paare je Gerät mit identischer Quelle.
  Aufnahme in Releasepakete wird erst nach einer Präferenz in mindestens zwei
  von drei Paaren ohne Regression der Rufrahmung bewertet.
- Validierung: `make blip25-test` (2/2), `cargo clippy -D warnings`,
  `make test` (OpenDMR und 137/137 .NET), `make docs`,
  Playground-Rundlauf mit 252 AMBE-Frames und 40.320 dekodierten Samples sowie
  NativeAOT-Publish des Servers für `osx-arm64`. Der Publish war erfolgreich;
  NuGet-Vulnerability-Abfrage war in der Sandbox nicht erreichbar und der
  macOS-Linker meldete die bekannte Abkündigung von `-ld_classic`.
- Präziser nächster Schritt: Änderungen committen, Branch pushen und einen
  Draft-PR mit Basis `feat/opendmr-playground-voice` erstellen. Danach einen
  separaten Branch `feat/nix-flake-release` auf diesem Stand anlegen, Issue #23
  konkretisieren und die Linux-Release-Erzeugung auf eine Flake umstellen.

## Active work: OpenDMR-Sprachszenarien

- Branch: `feat/opendmr-playground-voice`, Draft-PR #28, gestapelt auf
  `refactor/application-structure`/PR #27.
- OpenDMR basiert auf Version 1.0.0 und Commit
  `d28164b39ba4d91ad5948ff22707937f8944f70f`. DMRoute-ng kennzeichnet die
  lokalen Encoderkorrekturen als `1.0.0+dmroute.2`. Der Codec läuft als eigener
  Prozess; der Produktionsserver referenziert `DMRoute_ng.Audio` nicht.
- Der Upstream-Wrapper hatte die neun quantisierten Encoderparameter linear
  angeordnet. `MBEEncoder` liefert nun die bereits im OP25-Code definierte
  49-Bit-Kanalzuordnung direkt an die kanonische AMBE-Fehlerkorrektur.
- Der DMR-Wrapper verwendet nun wie der OP25-Sendepfad den logarithmischen
  Gain-Offset `3.0`. Erzeugung und Reset verwenden denselben Wert; die
  öffentliche dB-API rechnet relative Pegeländerungen in den Log2-Offset um.
  Die V/UV-Suche bildet die Drei-Harmonischen-Bänder des Analysators explizit
  auf die AMBE-Tabelle ab.
- `voice encode` erzeugt aus 8-kHz-Mono-PCM-WAV ein reproduzierbares
  `.dmrd`-Szenario für Gruppen- oder Privatrufe. `voice decode` verarbeitet
  rohe AMBE-Dateien oder einen vollständigen unverschlüsselten DMRD-Ruf.
- Explizites `--open-dmr` hat Vorrang vor `Codec:OpenDmrExecutable`, dem
  Programmverzeichnis und `PATH`. Bestehende Playground-Argumente bleiben
  gültig; Profile ohne `Codec` werden weiterhin geladen.
- `DmrVoiceBurstCodec` und `DmrVoiceBurstWriter` erzeugen drei kanonische
  AMBE-Frames je Burst, Voice-Sync, Full LC, Embedded LC und Terminatoren ohne
  verwaltete Allokationen im warmgelaufenen Pfad.
- `make opendmr-codec` baut in `artifacts/opendmr`; `make opendmr-test` prüft
  einen festen kanonischen AMBE-Frame. Linux-x64-Test- und Releasepakete
  enthalten Codec, Lizenz, Credits und vollständiges Quellarchiv; Windows
  bleibt ohne Codec nutzbar.
- Ein TG-1-Lauf mit dmroute.1 öffnete AnyTone und RT3S, blieb auf beiden
  Geräten aber schwer verständlich; im Wesentlichen war nur „Sprechgruppe 1“
  erkennbar. Der lokale Rundlauf klang vergleichbar. Gain 3 verbessert eine
  langsamere Kontrollansage, einzelne Laute bleiben jedoch verschluckt. Ein
  zusätzlicher 3,4-kHz-Filter war leicht schlechter und wird nicht verwendet.
  Der aktualisierte Befund steht in
  `docs/hardware-test-results-2026-10-26-voice.adoc`.
- Ein lokaler Referenzvergleich verwendet die deutsche ITU-T-P.50-Probe von
  DSPINI, deren DVSI-AMBE+2-DMR-Ausgabe, baocoder aus Commit `cef8125` und
  einen 360 Frames langen Baofeng-DM-32-Strom. Fremde Audiodaten und
  Capture-Vektoren bleiben wegen Lizenz und Größe außerhalb des Repositories.
- Die OpenDMR-9-Byte-Schnittstelle führt flache kanonische Codewörter; baocoder
  führt bereits DMR-interleavte Luftbits. Nach expliziter Konvertierung
  dekodieren beide Implementierungen den jeweils fremden Strom ohne FEC-Fehler.
  Die C#-Packung in `DmrVoiceBurstCodec` entspricht dieser Abbildung.
- Beim echten DM-32-Strom erreicht OpenDMR gegenüber JMBE eine
  Hüllkurvenkorrelation von 0,964 und eine Spektralkorrelation von 0,839;
  baocoder erreicht 0,975 und 0,844. Der OpenDMR-Decoder ist damit nicht die
  Hauptursache der Verständlichkeitsprobleme.
- Ein OpenDMR-kodierter P.50-Strom erreicht mit OpenDMR 0,552 und mit baocoder
  0,548 Spektralkorrelation, während die DVSI-Ausgabe 0,821 erreicht. Damit
  liegt der nächste technische Ansatzpunkt im Encoder-Analysator. Der gesamte
  Aufbau und die Messgrenzen stehen in
  `docs/voice-codec-reference-comparison.adoc`.
- Für `VC03` wurde `ansage-tg1.wav` mit OpenDMR `1.0.0+dmroute.2` neu in ein
  lokales Szenario mit 252 AMBE-Frames und 89 DMRD-Schritten kodiert. Der
  Rückweg enthält 40.320 Samples. SHA-256 des Szenarios:
  `70f9adaa7da3d4c3f3774bc9676d400b6e43788a8a4cbd3301ff0ea62d496bfc`.
- `hs1` wurde erfolgreich angemeldet und angepingt. Drei TG-1-Aussendungen an
  das verfügbare AnyTone liefen vollständig durch; anschließend wurde `hs1`
  regulär abgemeldet. Die Ansage war mit bekanntem Inhalt durchaus
  verständlich, könnte aber noch besser sein. `VC03` ist für Rufaufbau und
  Rufende bestanden, für die Sprachqualität ohne Blindprüfung nur teilweise.
- Die nativen Regressionstests prüfen nun Stille, ein warmgelaufenes
  synthetisches Sprachsignal, Reset, explizite 0 dB und unterschiedliche
  Gainwerte. `make test` besteht mit den nativen Tests und 136/136 .NET-Tests;
  `make docs` läuft ohne Warnung durch. Der Playground-Rundlauf mit
  `ansage-tg1.wav` erzeugt unter dmroute.2 252 AMBE-Frames und dekodiert sie zu
  40.320 PCM-Samples. Der NativeAOT-Publish des Servers für `osx-arm64` ist
  erfolgreich; `ld` meldet nur die bekannte Abkündigung von `-ld_classic`.
- Draft-PR #28 enthält diesen Stand und bleibt auf den noch offenen PR #27
  gestapelt. Nächster fachlicher Schritt ist die spätere Wiederholung von
  `VC04` mit einem durch dmroute.2 neu erzeugten Szenario auf dem RT3S. Ein
  experimenteller, bitgenauer Referenzencoder wird auf einem getrennten Branch
  als opt-in Backend vorbereitet und anschließend verdeckt gegen OpenDMR am
  AnyTone verglichen. Die lokalen WAV-/DMRD-Dateien bleiben unverändert und
  werden nicht committed. Auch das neue lokale `ansage-tg1-dmroute2.dmrd` wird
  nicht committed.

## Basis: gestapelter Struktur- und Dokumentations-PR

- Die Arbeit liegt auf `refactor/application-structure` und baut direkt auf
  `feat/issue-10-sds-echo` beziehungsweise Draft-PR #26 auf.
- Radio-Check-Resolver, Router, MQTT-Integration und SDS-Echo sind als partielle
  Klassen nach Verantwortung gegliedert. Methodenrümpfe, Signaturen,
  Protokollwerte, Timings und DI-Registrierungen bleiben unverändert.
- Fachliche Kommentare dokumentieren Span-Lebensdauer, Puffergrößen,
  Wire-Authentisierung, Auflösungsreihenfolge, Replay-Schutz und Zustandsbesitz.
- Nur berührte Dateien werden formatiert; unabhängige Tests und FEC-Tabellen
  bleiben aus dem Diff.
- The branch contains the confirmed outbound SDS transport and an internal echo service as the first hardware-testable slice of issue #10.
- The general external API and its delivery result contract are not implemented yet.
- Die capture-verifizierten Radio-Check-Codecs werden jetzt produktiv für die
  Rekonstruktion nach einer asynchronen Pfadsuche verwendet. Die ältere passive
  Auswertung auf `feat/issue-9-radio-check` bleibt davon getrennt.
- The branch includes a Playground extension with profile-backed virtual radios,
  bidirectional semantic SDS, hardware-compatible TMS delivery reports, Call
  Alert, Radio Check, bounded inboxes, service targets and an interactive line
  editor.
- A pre-existing user edit widens `VirtualRadio.ValidateRadioId` to `999991`;
  it was preserved unchanged and must not be silently attributed to the control
  feature.

## Objective

Die großen Anwendungsdienste ohne Verhaltensänderung nach Verantwortlichkeiten
gliedern und die nicht offensichtlichen Protokoll-, Speicher- und
Nebenläufigkeitsverträge im Code festhalten.

## Decisions

- Issue #10 starts with an internal echo service on reserved destination ID `990099`; this exercises the complete confirmed outbound transport without prematurely choosing the external API.
- Nur Radio Check startet den neuen Resolver. Call Alert, normale private SDS,
  Sprache und allgemeines Store-and-Forward bleiben außerhalb dieses Schritts.
- Die Reihenfolge ist lokaler Gast, Heimat-Master, aktive Blind-Probe über seine
  angemeldeten Hotspots außer dem Eingang, dessen frischere lokale oder
  Roaming-Beobachtung, Bestätigung des Roaming-Masters und schließlich WHOIS an
  alle bekannten direkten Peers. Es gibt kein Multi-Hop-Flooding.
- Ein Ziel-Master erzeugt fünf Control-Preambles und den Radio-Check-Request neu,
  behält aber Quell-/Ziel-ID bei. Der Master bestätigt niemals stellvertretend.
- Fallback-SDS verwendet standardmäßig Dienst `990098`, 24 Stunden TTL und eine
  persistente, auf 128 Einträge begrenzte JSON-Outbox. Der Ursprung löscht erst
  nach persistierter Annahme durch den Heimat-Master.
- Eine Fallback-SDS gilt nach einem positiven TMS-Delivery-Report oder einem
  positiven bestätigten DMR-Response-Header als zugestellt. Eine negative
  Quittung löscht den Outbox-Eintrag nicht.
- DMR destination IDs `990000` through `999999` are reserved for local services. Frames to these IDs are consumed locally and frames claiming a reserved source ID are rejected.
- The echo command is a group UTF-16LE SDS beginning with `ECHO`; the reply is a private confirmed SDS to the requesting five-digit radio ID.
- Echo requests are copied into bounded, preallocated slots, duplicate-suppressed for 30 seconds, processed sequentially, and retried up to three times while awaiting ACK/NAK.
- `Services:Echo:Enabled` controls the service and defaults to enabled.
- Issue #9 remains deferred on its separate branch until a second RF endpoint can capture the target response without the single-simplex-hotspot collision observed previously.
- Issue #15 follows both protocol efforts and turns their verified packet flows into semantic emulator scenarios.
- Issue #16 follows #15 so multi-master tests can use the mature scenario API. Issue #14 follows after that API stabilizes; hardware-coupled #17 remains last.
- `tools/DMRoute_ng.Emulator` is a .NET 9 core library with no reference to the DMRoute-ng server assembly or xUnit. Server and emulator consume the shared `DMRoute_ng.Protocol` codec assembly.
- `VirtualHotspot` owns the UDP session and models login, configuration, keepalive, disconnect, bounded DMRD capture and explicit protocol states. Its receive fan-out lets raw inspection and attached radios observe the same frame independently.
- `VirtualRadio` retains capture replay and now sends unconfirmed group SDS, sends confirmed private SDS with delivery-report/ACK/NACK retry, reassembles received TMS messages, acknowledges valid private transfers and stores text messages in a bounded inbox.
- A real FuG-to-FuG capture proved that positive AnyTone delivery is a complete
  private TMS transaction with payload `00 03 BF 00 01`, not the previously
  assumed single DMR response header. The latter remains a compatibility fallback.
- Gerätekompatibilität wird aus dem tatsächlichen Protokollverhalten bestimmt,
  nicht aus einem Herstellerprofil pro DMR-ID. Ein vollständiger positiver
  TMS-Delivery-Report und ein positiver bestätigter DMR-Response-Header gelten
  gleichwertig; die erste gültige Quittung beendet den Vorgang genau einmal.
- Confirmed emulator transactions use five CSBK preambles. Automatic delivery
  reports wait about 500 ms; REPL sends wait 2.5 seconds between attempts and
  the echo service waits three seconds.
- Captured Call Alert uses four control preambles, request `9f/10/00/00` and
  acknowledgement `a0/10/9f/00`. Captured Radio Check uses five control
  preambles and opcode `0x24`, with byte 3 changing from `0x00` to `0x80`
  in the positive response. Automatic control responses wait 120 ms.
- Virtual radios are declared in the Playground profile with a five-digit ID, hotspot, timeslot, talkgroups and inbox capacity. Private inbound routing is primed explicitly with a self-addressed `radio announce` CSBK.
- Production binding remains `0.0.0.0:62031`; internal test hooks allow port `0`, await socket readiness and trigger the existing 45-second repeater liveness sweep deterministically.
- Small decentralized deployment defaults: 256 active calls, 8,192 learned local routes, 128 SDS sessions, and 4,096 bytes per SDS message.
- When a bounded table is full, continue routing but do not add new tracking state. Increment a diagnostic counter instead.
- Packet callbacks are synchronous and borrow `ReadOnlySpan<byte>` data only for the duration of the callback.
- IPv4 remains the supported network family.
- MQTT uses only `RawMqttClient`, QoS 0, a hard cut from the legacy topic paths, non-retained event topics, and retained per-entity state topics.
- MQTT event buffering is bounded and keeps the newest events with `DropOldest`. State is rebuilt immediately after a reconnect and every ten seconds by default.
- The hotspot ID in telemetry always comes from the authenticated DMRD packet/repeater state; no runtime hotspot ID is hardcoded.
- Documentation is built as two stable deliverables: `DMRoute-ng-Handbuch.pdf`
  for operation and architecture, and `DMRoute-ng-Hardwaretests.pdf` for test
  plans and historical results. CI publishes both as an artifact; successful
  main/master releases attach them to the matching commit release.

## Current state

- `RadioCheckReachabilityCoordinator` übernimmt ausschließlich nicht routbare,
  valide Radio-Check-Requests aus einer begrenzten Value-Type-Queue. Laufende
  Anfragen und Retries werden zusammengeführt; positive und negative Ergebnisse
  besitzen kurze, begrenzte Caches.
- `MeshControlProtocol` ergänzt feste, versionierte und HMAC-authentifizierte
  96-Byte-DMCX-Pakete für Route Query/Result, Radio-Check-Ausführung und den
  vollständigen Fallback-SDS-Handoff. Der Mesh-Service akzeptiert Steuerpakete
  nur von bekannten Zonen und passender IPv4-Adresse.
- Lokale Heimat- und Gastpfade besitzen nun `LastSeen` und werden nach 15 Minuten
  beziehungsweise bei abgemeldetem Hotspot nicht verwendet. Der Heimat-Master
  wählt die frischere lokale oder Roaming-Beobachtung.
- Für ein unbekanntes Heimatziel sendet der Heimat-Master fünf Preambles und den
  Radio-Check-Request an alle anderen angemeldeten lokalen Hotspots und wartet
  500 ms auf die echte Antwort, bevor Roaming und WHOIS folgen.
- `FallbackSdsService` sendet `Verbindungsversuch von <Quell-ID>` als bestätigte
  private TMS-Nachricht mit drei Versuchen und 2,5 Sekunden Antwortzeit. Ein
  positives Delivery Result oder ein positiver bestätigter DMR-Response-Header
  entfernt die Outbox-Nachricht; sonst folgt frühestens nach fünf Minuten ein
  neuer Zustellzyklus.
- Der Setup-Wizard und die JSON-Konfiguration enthalten Aktivierung,
  Maximalzustand, Dienst-ID, TTL, Kapazität und Outbox-Pfad. Das deutsche
  Handbuch enthält eine eigene Radio-Check-/WHOIS-/Fallback-Referenz.
- `docs/radio-check-resolver-hardware-plan.adoc` enthält die konkrete
  Ein-Master-Abnahme RCR01-RCR05 für Zone 101 und die Zwei-Master-Matrix
  RCR06-RCR09. Das lokale ignorierte Playground-Profil besitzt dafür zusätzlich
  `r-local` mit ID `10102`; es wurde erfolgreich geladen, ohne vorhandene PSKs
  oder Hotspotwerte zu verändern.

- Added caller-owned writers for TMS/UDP/IP payloads, DMR CRCs, BPTC(196,96), rate-3/4 trellis bursts, confirmed data headers, response headers, CSBK preambles and complete DMRD packets.
- `SdsGateway` now publishes a borrowed-span `SdsMessageEvent` with source, destination, hotspot, group/private marker, encoding and receive timing. MQTT copies text into bounded slots and decodes it only on its background publisher.
- `SdsServiceRegistry` dispatches reserved destinations to `ISdsService` implementations; `SdsEchoService` sends confirmed replies and consumes matching acknowledgements.
- Dependency injection exposes `DmrServer` as `IDmrSender`, starts the echo background service and constructs the service registry during application startup.
- Reserved-service classification remains allocation-free after warm-up and prevents service traffic from being routed or learned as ordinary subscriber traffic.
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
- Added a German operator-first Quick Start, an SDS echo reference and an
  emulator reference. `docs/handbook.adoc` and `docs/hardware-tests.adoc` assemble
  the existing component and test documents into two books.
- Added reproducible AsciiDoc HTML/PDF targets backed by pinned Bundler
  dependencies. Generated files live below `artifacts/docs/` and remain
  untracked.
- Consolidated the issue-#9 Radio Check capture plan and the 2026-09-13
  one-hotspot findings into the hardware-test book. The plan now explicitly
  requires two independent RF endpoints after the antenna repair.
- Updated README, task planning, configuration, routing, MQTT, location,
  hot-path and emulator documentation for the current wizard and echo-service
  behavior.
- Added `DMRoute_ng.Playground`, an interactive terminal REPL over the independent
  emulator core. It connects multiple named virtual hotspots to an already
  running IPv4 master, supports session commands, raw DMRD, text capture
  scenarios and decoded receive output without exposing PSKs.
- Playground profiles use atomic JSON writes and Unix mode `0600`. Scenario
  replay validates source IDs and delays, bounds file content and normalizes only
  the copied outer repeater ID for the selected virtual hotspot.
- CI publishes experimental self-contained single-file Playground artifacts for
  Linux x64 and Windows x64; regular release assets remain unchanged.
- Added `radio list`, `radio announce`, `sds send`, `sds inbox`, `sds watch` and
  `sds clear` to the Playground REPL. Group SDS uses unconfirmed rate-3/4 data;
  private SDS uses the confirmed transport and automatic TMS delivery reports.
- Added semantic `radio alert` and `radio check` transactions plus
  `radio events`, `radio watch` and `radio clear`. Virtual radios
  automatically acknowledge valid addressed requests and retain decoded
  requests/responses in a separate bounded DropOldest inbox.
- The shared protocol codec now writes and validates Call Alert and Radio Check
  CSBKs, including inner/outer ID consistency, private data-sync framing,
  feature ID and masked CRC.
- The REPL accepts `service 990099`, rejects reserved IDs on the private `fug`
  path with actionable guidance, and provides Tab completion, in-memory
  Up/Down history and Ctrl+R reverse search on an interactive terminal.

## Validation

- Für den Struktur- und Dokumentationsstand sind die gezielte
  `dotnet format`-Prüfung aller berührten C#-Dateien, `git diff --check`,
  `make test` mit 130/130 Tests und der lokale NativeAOT-Publish für
  `osx-arm64` erfolgreich. Build und Tests melden 0 Warnungen.
- Release build succeeds with 0 warnings and 0 errors.
- `make test` passes 130/130 tests in Release mode with 0 build warnings.
- Codec tests reproduce captured UTF-16LE TMS datagrams, BPTC/trellis bursts and confirmed-message CRCs byte-for-byte.
- Service tests cover local consumption, reserved-source rejection, duplicate suppression, private confirmed reply delivery with ACK, and zero-allocation warmed classification.
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
- `make docs` validates every standalone AsciiDoc source and builds both HTML
  books and both PDFs without warnings.
- Both GitHub workflow files parse as YAML, and `git diff --check` reports no
  whitespace errors.
- Playground tests cover profile validation and permissions, setup PSK
  derivation, quoted commands, DMRD inspection, malformed scenarios, secret-safe
  output and a real two-hotspot login/ping/replay/receive/disconnect loop.
- Virtual-radio loopback tests cover padded unconfirmed group SDS, talkgroup
  filtering, simultaneous raw capture, explicit route announcement, confirmed
  private delivery with automatic TMS report and bounded retry timeout. An additional
  codec test round-trips the unconfirmed header, rate-3/4 block and outer
  group/timeslot flags.
- Golden-vector tests reproduce the 2026-09-21 FuG delivery-report datagram,
  confirmed header and three data bursts byte-for-byte. Warmed delivery-report
  ingestion allocates 0 managed bytes.
- Self-contained single-file publish smoke tests succeed for `linux-x64` and
  `win-x64`; each output contains the executable and both sanitized example
  scenarios. The local framework-dependent binary also returns the expected
  `--help` usage.
- `make test` passes 130/130 tests in Release. New coverage includes
  byte-exact hardware vectors, both control roundtrips, timeout/retry, bounded
  event overflow, command output and completion. Warmed parsing and complete
  `VirtualRadio` control ingestion including the bounded inbox allocate
  0 managed bytes.
- Neue Resolver-Regressionen prüfen DMCX-Roundtrip, HMAC-Manipulation und
  Zeitfenster, persistentes Laden/Deduplizieren/Ablaufen der Outbox, den normalen
  Heimat-Master-Fastpath und 0 verwaltete Bytes für 1.000 gewärmte unbekannte
  Radio Checks. Ein echter Emulator-Loopback empfängt außerdem die SDS
  `Verbindungsversuch von 10101` von `990098` und bestätigt ihren Delivery
  Report bis zum neuen Service zurück.
- Zwei weitere Loopback-Szenarien prüfen die erfolgreiche Blind-Probe zu einem
  unangekündigten Heimatziel über den zweiten Hotspot und den Timeout mit genau
  einem später zugestellten Outbox-Eintrag. Der RT3S-Golden-Vector bestätigt
  außerdem den positiven DMR-Response-Header; dessen gewärmte Auswertung und
  die Radio-Check-Antwortauswertung bleiben bei 0 verwalteten Bytes.
- Ein lokaler NativeAOT-Smoke-Publish für `osx-arm64` einschließlich der neuen
  source-generierten Outbox-Serialisierung ist erfolgreich. `make docs` baut
  Handbuch und Hardwaretest-PDF ohne Warnung.
- `make docs` validates all standalone AsciiDoc sources and rebuilds both
  HTML books and both PDFs without warnings. On this host the locked gems were
  supplied through temporary `BUNDLE_PATH=/private/tmp/dmroute-ng-bundle`.

## Hardware validation

- Der Ein-Master-Lauf vom 21. September 2026 ist in
  `docs/hardware-test-results-2026-09-21-radio-check-resolver.adoc`
  dokumentiert. Blind-Probe und Fallback-Zustellung erreichten den physischen
  Hotspot beziehungsweise das RT3S; der bekannte AnyTone-Pfad bestätigte Radio
  Check beim ersten Versuch.
- Das RT3S `10101` beantwortete den Radio Check nicht. Nach Empfang der
  Fallback-SDS sendete es einen positiven bestätigten DMR-Response-Header, den
  der Stand während des Laufs noch nicht auswertete. Der erfasste Frame ist nun
  Golden Vector und der Kompatibilitätspfad ist automatisiert geprüft.
- Die Rohmitschnitte liegen unter `/private/tmp` und enthalten 1.360 Pakete auf
  `en0` sowie 682 Pakete auf `lo0`, jeweils ohne Kernel-Drops. Die Test-Outbox
  wurde separat archiviert und die vor dem Lauf gesicherte Outbox mit ihren zwei
  ursprünglichen Einträgen nach `data/radio-check-outbox-zone101.json`
  zurückgelegt.
- Offen sind eine Hardware-Wiederholung des neuen Fallback-ACK-Pfads, eine
  positive Blind-Probe mit Radio-Check-fähigem FuG aus Zone 101 sowie RCR06 bis
  RCR09 über zwei Master für Heimatpfad, Roaming, WHOIS und Outbox-Übergabe.
- `docs/device-compatibility-hardware-plan.adoc` definiert `DC01` bis `DC10` für
  die getrennte AnyTone-/RT3S-Abnahme. Etappe A benötigt nur den funktionierenden
  physischen Hotspot plus Playground; Etappe B folgt mit zwei isolierten
  RF-Endpunkten nach Reparatur des zweiten Hotspots.

- Two hotspots and four radios are now available in Switzerland. One hotspot currently needs its loosened antenna connector resoldered; the other can be used for the first echo-service validation.
- Once the connector is repaired, the two independent RF endpoints can resume the deferred Radio Check captures from `feat/issue-9-radio-check`.
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
- The 2026-09-21 SDS captures prove that a real positive receipt is a complete
  TMS message. `docs/hardware-test-results-2026-09-21-sds.adoc` records the
  sanitized finding; raw PCAPs remain under `/private/tmp`.
- The 2026-09-21 response-focused captures prove complete Call Alert and Radio
  Check request/acknowledgement formats between `10001` and `10101`.
  `docs/hardware-test-results-2026-09-21-control.adoc` records the sanitized
  vectors; raw PCAPs remain under `/private/tmp`.

## Known limits

- WHOIS erreicht nur durch DMBC bekannte direkte Peers und besitzt keine
  Multi-Hop-Weiterleitung. Die erste Version wird nur durch Radio Check
  ausgelöst.
- Die Outbox ist dauerhaft, aber lokal dateibasiert. Ohne erreichbaren
  Heimat-Master bleibt die Ursprungsinstanz bis zum Ablauf der 24-Stunden-TTL
  für die Übergabe verantwortlich.
- Der mehrzonige Ablauf ist durch Codec-, Routing-, Persistenz- und Hotpath-Tests
  abgedeckt, aber noch nicht mit den zwei physischen Hotspots end-to-end
  bestätigt.
- Das im Ein-Master-Lauf verwendete RT3S beantwortete den Radio Check nicht;
  dadurch ist der positive Blind-Probe-Abschluss mit echter Hardware noch offen.

- The current issue-#10 slice exposes only the radio-facing echo service. It has no general HTTP, MQTT or command-line send API and no external delivery-state contract.
- Echo replies are serialized through one background worker; parallel outbound confirmed-SDS transactions are intentionally deferred until the general delivery model is specified.
- The emulator now has a capture-based interactive REPL plus semantic SDS,
  Call Alert and Radio Check. Semantic voice remains tracked in #15; a possible
  full-screen TUI remains in #14.
- The Playground requires an already running master. It does not start server
  processes, import PCAP files or emulate a local RF channel.
- Group frames are not returned to their origin hotspot, so two virtual radios
  attached to that same hotspot cannot use the master to hear each other's TG
  SDS. Use separate virtual hotspots for that routing test.
- UDP is intentionally IPv4-only, matching the server. Emulator receive queues
  are bounded; raw DMRD capture keeps the newest frames and reports its drop
  count, while control-queue overflow still fails visibly.
- Completed SMS strings and MQTT connection work may allocate by design.
- MQTT credentials and TLS are not implemented.
- A QoS-0 event already removed from the channel can be lost if the TCP publish fails. Retained registry state is reconstructed after reconnect; diagnostic frames longer than `Mqtt:MaxDiagnosticFrameBytes` are truncated.
- Topology snapshot replacement and initial repeater enrollment allocate outside the steady packet path.
- The hard allocation guarantee is covered for the listed synchronous processing paths; it does not attempt to include exception construction or enabled logging sinks.
- The privacy raster applies at the MQTT boundary only. Exact GPS text remains visible to local logging, and IDs, timestamps, speed, course, altitude, and coarse movement remain visible on MQTT. The mechanism reduces precision but is not anonymization or cryptographic location protection.
- An RMC `A` status proves only what the radio reports. The tested AnyTone also labels its configured fixed beacon as valid, so the radio must use `APRS -> Upload Beacon -> GPS Beacon` for a satellite-derived position.

## Precise next step

Die CI-Prüfungen und das Review von Draft-PR #28 abwarten. Nach dem Merge der
gestapelten Basis #27 den PR auf den dann aktuellen Zielbranch umstellen. Issue
#15 ist um Implementierung, PR und die weiterhin offenen Hardwarefälle `VC03`
bis `VC09` ergänzt und bleibt bis zu deren Durchführung geöffnet.
