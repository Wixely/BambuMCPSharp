## Summary

Describe the change and why it is needed.

## Safety impact

Describe any effect on printer commands, file transfers, authentication, certificate validation, or safety gates. Write "None" if there is no effect.

## Verification

- [ ] `dotnet build BambuMCPSharp.sln -c Release`
- [ ] Specification runner passes, or the reason it was not run is documented
- [ ] No credentials, printer identifiers, private addresses, local paths, logs, snapshots, sliced files, or generated G-code are included
- [ ] Real-printer actions, if any, were explicitly authorized and are described below

## Real-printer testing

List only non-sensitive actions and results. Write "Not performed" if no real printer was used.
