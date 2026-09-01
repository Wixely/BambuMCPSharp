# Roadmap

Last updated: 2026-09-01

Next review: 2026-11-25

This roadmap keeps slicing and G-code generation outside BambuMCPSharp. The server may inspect and dispatch an already-sliced file, but it must not rewrite toolpaths or silently broaden permission to heat, move, print, delete, or update a real printer.

## Next implementation priority

- [ ] **End-to-end X1 Skip Parts workflow** — Owner: TBD. Make the X1/X1C mid-print “Skip Parts” feature safely usable without guessing numeric object IDs. Extend project inspection to catalogue the selected plate's object IDs and names, correlate them with the active print where the firmware exposes enough state, and report already-skipped and remaining parts. Before sending the existing `print.skip_objects` command, require an active compatible print, reject unknown or duplicate IDs, refuse a request that would skip every remaining part, and return the exact requested objects plus the printer acknowledgement and a bounded verification result. Validate the payload and behavior on the X1C with a deliberately prepared multi-object test print.

## P0 — safe print hand-off

- [x] **Bounded project-inspection foundation** — Completed: 2026-08-25. `bambu_inspect_project` handles `.gcode.3mf`, `.3mf`, and standalone `.gcode` files inside the transfer directory. It bounds file size, archive entries and expanded bytes; discovers plates; checks Bambu header/config/executable blocks and target model; and reports nozzle, printable bounds, bed, filament, time, layer, and digest metadata without uploading or executing the file.
- [ ] **Preflight enforcement and project enrichment** — Owner: TBD. Make upload/start consume a successful inspection of the same digest. Add bounded thumbnail, filament-use estimate, and object-ID extraction; compare the requested plate and configured/observed nozzle; and reject unsafe mismatches before upload or start.
- [ ] **Prepare/confirm/start workflow** — Owner: TBD. Add `bambu_prepare_print` and a separate confirmation call. Bind a short-lived confirmation token to the printer alias, local file digest, remote path, plate, AMS mapping, and calibration choices so a changed file or request cannot reuse approval. Keep `AllowStartPrint` off by default and require the printer to report idle immediately before dispatch.
- [ ] **Verified, non-destructive upload** — Owner: TBD. Refuse overwrites by default, upload to an MCP-owned temporary name, verify remote size and a digest when the endpoint supports one, then rename into place. Bound retries and clean up only MCP-owned temporary files.
- [ ] **Verified print-start outcome** — Owner: TBD. After MQTT acknowledgement, wait for a bounded `IDLE -> PREPARE -> RUNNING` transition or return the definite failure, acknowledgement reason, and decoded HMS state. Never equate an MQTT acknowledgement with a started print.
- [ ] **Standalone X1C G-code dispatch** — Owner: TBD. Add a separately gated `bambu_start_gcode_file` using the firmware's `print.gcode_file` command for an already-sliced, preflighted SD-card file. Validate the exact X1C path convention with an upload-only test, require manual machine preparation and explicit approval for the first real start, and do not generate or modify G-code.
- [ ] **AMS and external-spool preflight** — Owner: TBD. Resolve every project filament to a live tray or the external spool, validate tray presence and compatible material temperature ranges, and emit the firmware-appropriate `ams_mapping`/`ams_mapping2` representation. Refuse ambiguous or incomplete mappings.
- [ ] **Certificate pinning for every printer channel** — Owner: TBD. Extend the existing camera pin model to MQTT and FTPS, provide a deliberate first-observation/pin workflow, and report certificate changes without exposing fingerprints or access codes in logs.

## P1 — monitoring and recovery

- [ ] **Wait-for-state tool** — Owner: TBD. Add a cancellable, bounded `bambu_wait_for_state` for preparing, running, paused, finished, and failed transitions so clients do not need aggressive polling.
- [ ] **Structured print-event history** — Owner: TBD. Keep a bounded local history of job transitions, acknowledgement outcomes, HMS changes, and recovery actions without storing access codes, printer addresses, serials, certificate fingerprints, or G-code contents.
- [ ] **SD-card capacity and safe cleanup** — Owner: TBD. Report available space and identify MCP-owned stale temporary uploads. Deletion remains separately gated and never targets user-created files automatically.
- [ ] **AMS operations** — Owner: TBD. Add separately gated load, unload, retry, and tray-setting tools with live-state checks and bounded temperature policies. These operations heat and move hardware and remain off by default.
- [ ] **Supported recovery and detection options** — Owner: TBD. Add typed controls for firmware-reported options such as automatic recovery, air-print detection, filament-tangle detection, nozzle-blob detection, and sound where the connected model advertises support. Avoid a generic MQTT escape hatch.
- [ ] **Richer read-only health** — Owner: TBD. Surface nozzle diameter, installed capability flags, maintenance indicators, SD-card state, camera state, and relevant detection/recovery settings in a stable parsed response.
- [ ] **Investigate X1Plus/SSH recovery for MC-owned HMS faults** — Owner: TBD. Determine whether X1Plus or the printer's local Linux environment exposes a supported, bounded motion-controller acknowledgement or reboot operation for persistent HMS alerts such as heatbed force-sensor faults. Distinguish clearing a resolved latched alert from restarting the AP, hiding UI state, bypassing a safety check, or factory-resetting the MC. Review and document the protocol before any printer test; do not add raw packet injection or a generic HMS-clear tool without a verified command, explicit safety constraints, and user approval for the exact test.

## Explicit non-goals

- Slicing, generating, translating, repairing, or optimizing G-code.
- Native Bambu libraries, downloaded networking plugins, P/Invoke, or external helper processes.
- Firmware upgrades or downgrades through MCP.
- Unrestricted MQTT publication or automatic execution of arbitrary G-code.
- Autonomous print start, stop, deletion, or motion based only on a timer or vision-model decision.
- Cloud-account integration when the offline LAN workflow is sufficient.

## Release criteria for print-start enablement

Before recommending `AllowStartPrint=true`, the P0 inspector, confirmation binding, verified upload, AMS/external-spool validation, and verified state transition must have bounded unit tests. The exact outgoing MQTT payload must be reviewed with redacted fixtures, followed by an upload-only real-machine test. A first real print start requires the user to prepare and inspect the machine manually and explicitly approve that specific validated job.
