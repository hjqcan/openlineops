# Headless Runner

The `runner` release artifact starts a completed automation project without
opening Studio. It is a self-contained `win-x64` bundle and does not require a
separately installed .NET runtime.

Verify the outer release manifest and checksum set before extraction, then
verify the exact payload described by `bundle-manifest.json` and
`bundle-checksums.sha256`. Production releases require a valid Authenticode
signature on `OpenLineOps.Runner.exe`.

Run one immutable published Project Snapshot with:

```powershell
.\OpenLineOps.Runner.exe run C:\Projects\LineA `
  --snapshot active `
  --production-unit-id 8a9d9629-598e-4e96-a8e7-5df8d7da44a9 `
  --identity UNIT-001 `
  --actor operator-a
```

The target may be a Project directory or its `.oloproj` file. `--snapshot`
defaults to `active`; it may also name an immutable snapshot. The production
unit ID, external identity, and actor are required. `--run-id` may supply the
non-empty canonical GUID used for an idempotent caller-owned run request.

The process writes machine-readable JSON status and returns stable process exit
codes. Cancellation through Ctrl+C requests a controlled stop. Use a service,
scheduler, MES adapter, or operator launcher to invoke the executable under an
identity that has only the Project, runtime state, device, and artifact access
required by that automation line.
