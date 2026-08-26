# Contributing to BambuMCPSharp

Thanks for helping improve BambuMCPSharp. This is an MIT-licensed public GitHub project.

## Before opening a pull request

1. Open an issue for changes that materially alter the protocol, safety model, dependencies, or architecture.
2. Keep changes focused and do not include printer credentials, serial numbers, private addresses, local paths, logs, snapshots, sliced files, or generated G-code.
3. Preserve the managed-code-only dependency closure. Native libraries and P/Invoke are out of scope.
4. Build the solution and run the specification project:

   ```sh
   dotnet build BambuMCPSharp.sln -c Release
   dotnet run --project tests/BambuMCPSharp.Specs/BambuMCPSharp.Specs.csproj -c Release --no-build
   ```

5. Explain the behavior change, safety impact, and verification performed in the pull request.

## Real-printer testing

A real printer is safety-critical hardware. Automated tests must remain read-only unless the person operating the printer explicitly authorizes a specific write action.

- Do not start or stop a print, heat, move axes, upload or delete files, send raw G-code, or run calibration as an implicit test step.
- Never generate G-code for hardware testing. Ask the printer operator to supply any required sample.
- Keep `Bambu:ReadOnly=true` unless an explicitly authorized test requires otherwise, and enable only the narrowest additional gate.
- If a test could require clearing the bed, removing a print, loading filament, or another physical action, pause and ask the operator to perform it.
- Do not weaken certificate validation or log access codes to make a test pass.

## Pull requests

By submitting a contribution, you agree that it is licensed under the repository's [MIT License](LICENSE). Keep commits understandable and use clear, imperative commit subjects.
