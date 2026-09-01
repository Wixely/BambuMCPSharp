# BambuMCPSharp

[![MIT license](https://img.shields.io/github/license/Wixely/BambuMCPSharp)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/Wixely/BambuMCPSharp)](https://github.com/Wixely/BambuMCPSharp/releases/latest)
[![Release build](https://github.com/Wixely/BambuMCPSharp/actions/workflows/build-release-packages.yml/badge.svg)](https://github.com/Wixely/BambuMCPSharp/actions/workflows/build-release-packages.yml)

A standalone C# **MCP (Model Context Protocol) server** for **Bambu Lab printers in offline LAN mode**, built for and validated on the **X1 Carbon (X1C)**. It speaks the printer's three LAN interfaces directly — MQTT for state and commands, implicit FTPS for the SD card, and the RTSPS chamber camera for snapshots — so an agent (or a computer-vision model watching the snapshots) can monitor a print, catch failures, and act.

**Slicing is out of scope.** Another system produces the `.3mf`; this server uploads it, starts it, watches it, and photographs it. No Bambu Cloud, no account, no `bambu_networking.dll` — LAN mode and the access code only. 100% managed C#, MIT-licensed, MIT/Apache-2.0 dependencies, no native code anywhere in the closure.

Default port: **5718**. MCP endpoint: `http://localhost:5718/mcp`.

## Features

- **Monitoring built for watch-loops.** `bambu_status` returns a parsed digest — job state, progress, layers, minutes remaining, decoded stage, temperatures, fans, light, AMS, active errors — that an agent can poll every few seconds. `bambu_status_raw` exposes the full firmware report when the digest is not enough.
- **Camera snapshots for computer vision.** `bambu_camera_snapshot` captures a PNG keyframe from the X1C's RTSPS stream (port 322, "LAN Mode Liveview"), decoded entirely in managed code, and returns it as MCP image content and/or saves it to disk — ready to feed a vision model such as Qwen. During a multi-part print, the intended recovery loop is for an agent to identify a failing part from the camera, map it to inspected project metadata, and use X1 Skip Parts to preserve the other parts.
- **One authoritative error check and bounded acknowledgement.** `bambu_errors` returns both error channels together: decoded `HMS_xxxx_xxxx_xxxx_xxxx` alerts with their Bambu wiki links and the current `print_error` with its exact acknowledgement context. It also includes relevant stage, temperature, fan, storage, camera, and connectivity state. A separate off-by-default tool can acknowledge only the exact current `print_error` after its physical cause is confirmed resolved.
- **Full job control, layered safely.** Pause/resume by default once writes are enabled; stop, start, temperatures, motion, raw G-code, and calibration each behind their own gate, off by default.
- **SD-card file management** over implicit FTPS: list, download, upload (the hand-off point from the slicing system), delete. Local files are confined to a configured transfer directory — the agent names files, never paths.
- **Bounded sliced-file inspection.** `bambu_inspect_project` reads an existing `.gcode.3mf`, `.3mf`, or standalone `.gcode` from the transfer directory without uploading or executing it. It checks Bambu block structure, target printer model, plates, nozzle metadata, build dimensions, bed type, filament types, time, layers, Skip Parts object IDs/names, and archive expansion limits.
- **Read-only by default** — nothing touches the printer until you set `Bambu:ReadOnly=false`, and the dangerous tail needs a second gate on top.
- **Multiple printers** by alias, for the day this grows into a farm.
- Configuration via `BambuMCPSharp.json`, environment variables (`BAMBUMCP_` prefix), or command line.
- Serilog logging to console and rolling files. Runs as console app, Windows Service, or Docker container.

## Quick start

On the printer: **Settings → Network → LAN Only Mode** (note the **access code**), and enable **LAN Mode Liveview** for the camera.

For `SerialNumber`, use the printer's actual serial from **Settings → General → Device info** on the printer itself. It looks like `00M00***********`. Do not use the separate **Device** identifier, which looks like `xxx-xxx-xxx`; that identifier does not form the printer's MQTT topics.

```sh
dotnet run -- \
  --Bambu:Printers:0:Host=192.168.1.100 \
  --Bambu:Printers:0:SerialNumber=00M00A0A0000000 \
  --Bambu:Printers:0:AccessCode=12345678 \
  --Bambu:ReadOnly=false
```

Point your MCP client at `http://localhost:5718/mcp`. `http://localhost:5718/healthz` confirms the server is up and lists the configured printers.

A typical first exchange: `bambu_list_printers`, then `bambu_status` to see what the machine is doing, `bambu_set_chamber_light on=true`, and `bambu_camera_snapshot` to look inside.

A typical print cycle (with the gates opened): drop `model.gcode.3mf` into the transfer directory → `bambu_inspect_project localName=model.gcode.3mf` → `bambu_upload_file localName=model.gcode.3mf` → `bambu_start_print file=/model.gcode.3mf` → poll `bambu_status` and `bambu_camera_snapshot` → on trouble, `bambu_pause_print` (or `bambu_stop_print` if `AllowStopPrint` is on).

### Docker

```sh
docker run --rm -p 5718:5718 \
  -e BAMBUMCP_Bambu__Printers__0__Alias=x1c \
  -e BAMBUMCP_Bambu__Printers__0__Host=192.168.1.100 \
  -e BAMBUMCP_Bambu__Printers__0__SerialNumber=00M00A0A0000000 \
  -e BAMBUMCP_Bambu__Printers__0__AccessCode=12345678 \
  -e BAMBUMCP_Bambu__ReadOnly=false \
  -e BAMBUMCP_Server__Password=change-me \
  ghcr.io/wixely/bambumcpsharp:latest
```

The container needs LAN access to the printer (ports 8883, 990, 322), so prefer host networking or a macvlan over publishing ports from an isolated bridge.

### Windows Service

```bat
sc.exe create BambuMCPSharp binPath= "C:\Services\BambuMCPSharp\BambuMCPSharp.exe"
```

## The printer's LAN surface

All three channels authenticate with the same LAN access code (user `bblp`); the printer presents a self-signed certificate on each:

| Service | Port | Used for |
| --- | --- | --- |
| MQTT over TLS | 8883 | Status reports (`device/{serial}/report`), commands (`device/{serial}/request`) |
| Implicit FTPS | 990 | SD-card files |
| RTSPS | 322 | Chamber camera ("LAN Mode Liveview" must be enabled) |

The access code is the only secret. It is never logged and never echoed by any tool. It changes whenever LAN mode is toggled on the printer — if everything suddenly returns authentication errors, re-read it from the printer screen.

## Tools (31)

| Area | Tools |
| --- | --- |
| Printers | `bambu_list_printers`, `bambu_printer_health` |
| Status | `bambu_status`, `bambu_status_raw`, `bambu_version`, `bambu_errors`, `bambu_ams_status` |
| Control | `bambu_pause_print`, `bambu_resume_print`, `bambu_clear_print_error`, `bambu_stop_print`, `bambu_skip_objects`, `bambu_set_print_speed`, `bambu_set_nozzle_temp`, `bambu_set_bed_temp`, `bambu_set_part_fan`, `bambu_set_aux_fan`, `bambu_set_chamber_fan`, `bambu_set_chamber_light`, `bambu_home_axes`, `bambu_jog`, `bambu_send_gcode`, `bambu_run_calibration` |
| Print jobs | `bambu_start_print` |
| Files | `bambu_inspect_project`, `bambu_list_files`, `bambu_download_file`, `bambu_upload_file`, `bambu_delete_file` |
| Camera | `bambu_camera_snapshot`, `bambu_camera_check` |

Command tools report `acknowledged` when the printer echoes the command's sequence id, and say so explicitly when it does not — an unacknowledged command usually still executed, so the tool tells the agent to verify with `bambu_status` rather than pretending to know.

## Safety model

`Bambu:ReadOnly` (default **true**) is the master switch; every gate below additionally requires it to be false. Defaults follow blast radius:

| Gate | Default | Guards |
| --- | --- | --- |
| `AllowPrintControl` | true | pause, resume, skip objects |
| `AllowStopPrint` | **false** | cancelling the job — hours of work and the material in it |
| `AllowStartPrint` | **false** | heating and moving an unattended machine |
| `AllowSpeedControl` | true | speed level (silent/standard/sport/ludicrous) |
| `AllowTemperatureControl` | **false** | manual nozzle/bed temps — clamped to `MaxNozzleTempC`/`MaxBedTempC`, refused mid-print |
| `AllowFanControl` | true | part / aux / chamber fans |
| `AllowLightControl` | true | chamber light |
| `AllowMotionControl` | **false** | homing and jogging — refused mid-print regardless |
| `AllowRawGcode` | **false** | arbitrary G-code |
| `AllowCalibration` | **false** | calibration runs |
| `AllowFileUpload` | true | SD-card uploads (the slicer hand-off) |
| `AllowFileDelete` | **false** | SD-card deletion |
| `AllowErrorClear` | **false** | acknowledging the exact current printer error after its physical cause is resolved |

Every refusal names the exact config key to change. File transfers are confined to `Bambu:FileTransferDirectory` (default `transfers/`), snapshots to `Bambu:SnapshotDirectory` (default `snapshots/`), both relative to the executable unless absolute.

### Error diagnostics and acknowledgement

Use `bambu_errors` as the single error check. It is read-only and always reports both `errors.hms` and `errors.printError`, plus relevant printer state and safe next-action guidance. An empty HMS list and a null print error mean neither error channel is active. HMS alerts do not have a generic clear command; resolve their underlying conditions and the printer removes them.

`bambu_clear_print_error` mirrors Bambu Studio's printer-side `clean_print_error` acknowledgement. It deliberately requires all of the following:

1. `Bambu:ReadOnly=false`.
2. `Bambu:AllowErrorClear=true` (off by default).
3. The exact decimal `errors.printError.code` returned by the latest `bambu_errors` call.
4. `confirmPhysicalCauseResolved=true`.

The command is refused when the active error has changed or disappeared. Acknowledging an error does not repair the fault, does not clear unrelated HMS entries, and may allow a paused job to proceed; verify both channels again with `bambu_errors`.

### X1 Skip Parts

`bambu_inspect_project` reports each plate's `parts` with the Bambu `identifyId`, display name, and whether it was already excluded when sliced. `partsSafelyAddressable=true` means the sliced plate contains unique IDs and has object labelling/exclusion enabled.

During a matching running or paused X1/X1C job, call `bambu_skip_objects` with the inspected `localName`, one-based `plate`, and one or more `objectIds`. The tool re-inspects the local project immediately before the command and refuses unknown or duplicate IDs, an active-job filename mismatch, parts already listed in the printer's `s_obj`, unsafe slicer metadata, or a request that would skip every remaining part. Its result names the requested and remaining parts and reports whether a refreshed printer state contains the requested IDs. `bambu_status` also exposes the current `skippedObjectIds`.

Skipping is irreversible for the current print. If every remaining part should stop, use the separately gated `bambu_stop_print`; Skip Parts intentionally cannot be used as an indirect stop command.

The intended vision-guided recovery workflow is:

1. An agent monitors `bambu_status` and periodic `bambu_camera_snapshot` images.
2. When one part appears detached, warped, or is producing spaghetti, pause the print with `bambu_pause_print` before spending more material or risking nearby parts.
3. Use `bambu_inspect_project` to obtain the selected plate's named parts and `identifyId` values.
4. Correlate the failing physical part in the camera image with the corresponding sliced-project part.
5. Call `bambu_skip_objects` for only that part, confirm its ID appears in `skippedObjectIds`, inspect another camera frame, and then resume the print.

The command and verification pieces are implemented. Fully automatic visual correlation is not yet guaranteed: the current inspector exposes names and IDs, but not each part's plate bounding box or an X1C camera-to-bed transform. Until that spatial mapping is added and validated, an agent should skip only when the failing part can be identified unambiguously from its name, known plate layout, or explicit user confirmation.

## Configuration

See [BambuMCPSharp.json](BambuMCPSharp.json) for the full annotated default. Put machine-local secrets (the access code) in `BambuMCPSharp.Local.json` — it is gitignored — or in environment variables.

Startup logs the operating posture:

```text
BambuMCPSharp startup
  Endpoint: http://localhost:5718/mcp
  Transport: HTTP (Streamable)
  Mode: Console
  Read-only: False
  Printer: x1c (default) 192.168.1.100 (X1C, serial ***********4321, access code 8 chars)
  Print control: True   stop: False   start: False   speed: True
  Temps: False (max nozzle 300C, bed 110C)   fans: True   light: True
  Motion: False   raw G-code: False   calibration: False
  File upload: True   delete: False
  Error acknowledgement: False
  Transfers: transfers   snapshots: snapshots
```

### MCP endpoint password

Set `Server:Password` to require authentication on `/mcp`. Clients send it as `X-MCP-Password`, `Authorization: Bearer`, or HTTP Basic (password part).

## Building

Requires the .NET 10 SDK. The FTPS adapter (`Wixely.FluentFTP.BouncyCastle`) and camera packages (`BambuLab.X1Camera`, `BambuLab.X1Camera.Imaging`, and transitively `H264Sharp.Decoder`) come from the **Wixely GitHub Packages feed**, and GitHub requires authentication even for public packages. One-time setup with a token carrying `read:packages`:

```sh
dotnet nuget update source GitHub-Wixely-Packages \
  --username <github-user> --password <token> --store-password-in-clear-text \
  --configfile NuGet.config
```

(or configure the credential user-level so it applies to every clone). Then:

```sh
dotnet build BambuMCPSharp.sln
```

CI and the Docker build authenticate the feed with the workflow's `GITHUB_TOKEN` / a BuildKit secret — see [build-release-packages.yml](.github/workflows/build-release-packages.yml) and the [Dockerfile](Dockerfile).

## Troubleshooting

- **MQTT connect fails** — printer powered? On this LAN? In LAN mode? Access codes rotate when LAN mode is toggled.
- **Camera times out** — "LAN Mode Liveview" is a separate switch from LAN mode itself; enable it on the printer screen. `bambu_camera_check` verifies the path end to end.
- **Snapshots are black** — the chamber light is off: `bambu_set_chamber_light on=true`.
- **FTPS fails mid-transfer** — the printer's FTP daemon is single-user-grade; retry, and avoid concurrent transfers to the same printer.
- **`bambu_start_print` acknowledged but nothing happens** — the SD path is case-sensitive and must be exact; confirm with `bambu_list_files`, and check both error channels with `bambu_errors`.

The LAN protocol is not an official Bambu contract and can shift with firmware updates; the protocol layer is isolated in `Services/` for exactly that reason.

Planned safety and workflow improvements are tracked in [ROADMAP.md](ROADMAP.md).

## Contributing and security

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request, especially the rules for tests involving a real printer.

For security vulnerabilities, follow [SECURITY.md](SECURITY.md) and do not publish credentials, printer details, or exploit instructions in a public issue.

## License

MIT — see [LICENSE](LICENSE). Third-party attributions in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
