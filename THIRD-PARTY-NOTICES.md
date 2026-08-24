# Third-party notices

BambuMCPSharp is MIT-licensed. Every runtime dependency is MIT (or Apache-2.0) per the
family licensing policy, and the entire dependency closure is managed .NET code — no
native libraries, no P/Invoke.

## ModelContextProtocol.AspNetCore 1.3.0

- Source: <https://github.com/modelcontextprotocol/csharp-sdk>
- License: MIT
- Purpose: MCP server implementation and Streamable HTTP transport.

## MQTTnet 5.2.0.1603

- Source: <https://github.com/dotnet/MQTTnet>
- License: MIT
- Purpose: MQTT 3.1.1 over TLS to the printer on port 8883 — status reports and commands.

## FluentFTP 54.2.0

- Source: <https://github.com/robinrodricks/FluentFTP>
- License: MIT
- Purpose: Implicit FTPS (port 990) to the printer's SD card, including the TLS session
  reuse its FTP daemon requires on data connections.

## BambuLab.X1Camera and BambuLab.X1Camera.Imaging 0.1.0-alpha.3

- Source: <https://github.com/Wixely/BambuX1Camera>
- Package source: <https://nuget.pkg.github.com/Wixely/index.json>
- License: MIT
- Purpose: RTSPS transport for the X1-series LAN camera (port 322) and pure-managed
  H.264 keyframe decoding to PNG for the snapshot tools.

## H264Sharp.Decoder 1.0.2 (transitive, via BambuLab.X1Camera.Imaging)

- Source: <https://github.com/Wixely/H264Sharp/tree/v1.0.2>
- Package source: <https://nuget.pkg.github.com/Wixely/index.json>
- Primary license: MIT
- Purpose: Pure-managed H.264 GOP/keyframe decoding, YUV-to-RGB conversion, and PNG
  encoding.

H264Sharp's `CabacInitTable.cs` header and `LICENSE-3RDPARTY.md` preserve a precautionary
BSD-2-Clause attribution to Cisco OpenH264 for CABAC context-initialization values
corresponding to ITU-T H.264 Tables 9-12 through 9-24. The same attribution states that
formulas, syntax structures, and lookup tables defined directly by the H.264 specification
are not subject to third-party copyright; the upstream README clarifies the values are
standard-mandated and the attribution does not change H264Sharp's MIT licensing. This
project retains the attribution; no OpenH264 source or binary is copied, linked, packaged,
or distributed.

## Serilog family

- `Serilog.AspNetCore` 10.0.0, `Serilog.Settings.Configuration` 10.0.0,
  `Serilog.Sinks.Console` 6.1.1, `Serilog.Sinks.File` 7.0.0,
  `Serilog.Enrichers.Environment` 3.0.1, `Serilog.Enrichers.Process` 3.0.0,
  `Serilog.Enrichers.Thread` 4.0.0
- Source: <https://github.com/serilog>
- License: Apache-2.0
- Purpose: Structured logging to console and rolling files.

## Microsoft.Extensions.Hosting.WindowsServices 10.0.8

- Source: <https://github.com/dotnet/runtime>
- License: MIT
- Purpose: Windows Service hosting support.
