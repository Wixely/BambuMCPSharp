# BambuMCPSharp v0.2.1

Released: 2026-09-17

## Changes

- Replace `Wixely.FluentFTP.BouncyCastle` with the official `FluentFTP.BouncyCastle` 1.0.0 package from nuget.org, including the upstream certificate hostname-validation fixes.
- Upgrade FluentFTP from 54.2.0 to 55.0.0.
- Use the upstream adapter namespace and `BouncyCastleFtpConfig`, preserving the printer's required TLS session resumption and legacy-resumption settings.
- Remove the old adapter's GitHub Packages mapping and update dependency documentation and license notices. Camera dependencies still use the authenticated Wixely GitHub Packages feed.

## Compatibility

No MCP tool or configuration changes are required. The existing printer certificate policy and safety gates are unchanged. This dependency update does not introduce stricter certificate checking for existing printer configurations.

## Validation

- Windows/.NET 10 Release build: zero warnings and errors; all 19 application specifications passed.
- Confirmed that the restored adapter matches the published nuget.org package rather than the earlier locally built release candidate.
- Published packages passed synchronous and asynchronous printer upload/download tests with exact byte matches and verified cleanup.
- Tested the application's actual `BambuFtp.WithClientAsync` path on a Bambu Lab X1 Carbon: three directory listings and unique test-file uploads of 1 byte, 32,769 bytes, and 4,194,305 bytes.
- Downloaded each application test file twice over fresh connections; all byte comparisons and SHA-256 checks matched. All 12 data connections resumed TLS across 16 fresh control connections.
- Deleted every test file and verified its absence. Existing printer files were not overwritten. Printer status remained unchanged.

Live hardware validation was performed on Windows. Linux hardware operation and physical printing were not tested for this release.
