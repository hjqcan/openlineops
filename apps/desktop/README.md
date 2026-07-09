# OpenLineOps Desktop

Electron desktop shell for the OpenLineOps local API.

## Commands

```powershell
npm install
npm run typecheck
npm run build
npm run package:win
npm run smoke:e2e
npm run dev
```

If Electron binary download is blocked or slow, retry installation with:

```powershell
$env:ELECTRON_MIRROR = "https://npmmirror.com/mirrors/electron/"
npm install
```

## Runtime Boundary

- The renderer has no Node.js access.
- Backend lifecycle and HTTP calls go through the preload `contextBridge`.
- SignalR connects to `OpenLineOps.Api` at `/hubs/runtime-progress`.
- API base URL defaults to `http://localhost:5135` and can be overridden with `OPENLINEOPS_API_BASE_URL`.
- Backend project path can be overridden with `OPENLINEOPS_API_PROJECT`.
- Repository root can be overridden with `OPENLINEOPS_REPO_ROOT`.

## Packaging

`npm run package:win` builds the renderer and Electron main/preload code, then
creates an unsigned Windows unpacked development package under
`release/desktop/win-unpacked`. The package contains the Electron desktop shell
only; the .NET API remains a separate release artifact or a source-run backend
selected with `OPENLINEOPS_REPO_ROOT` or `OPENLINEOPS_API_PROJECT`.

Signed installer or portable packaging is still required before a public
production desktop release.

## First Slice

The first desktop screen covers:

- API process start/stop from Electron main process.
- Platform and health status.
- Runtime station status resync.
- SignalR runtime timeline updates.
- Alarm acknowledgement.
- Trace row preview from Traceability APIs.
- Processes workbench for creating, loading, editing, validating, and publishing process graphs.
- Node toolbox for PythonScript, Command, Decision, Delay, and End nodes.
- Transition editor for maintaining process graph edges.
- Published process runtime launch form bound to engineering configuration snapshots.
- Engineering workbench for creating workspaces, projects, recipes, station profiles, and published configuration snapshots through backend contracts.
- Devices workbench for registering device definitions and station-bound instances, then managing connection, disconnection, fault, and reset state through backend contracts.
- Trace workbench for engineering trace search, facets, record details, measurements, artifacts, audit entries, and export package retrieval through Traceability APIs.
- Plugins workbench for plugin package discovery, manifest validation, capability and command inventory, lifecycle start/stop, and external process event inspection through backend contracts.
- Official Blockly workspace editing for PythonScript automation blocks, including axis movement, light output, motor rotation, wait, result blocks, and persisted/versioned user-registered custom blocks backed by Python code templates.
- Blockly-generated automation plans are executed by the backend runtime through the existing simulator, device-backed, or plugin-backed command route.

## Smoke Test

`npm run smoke:e2e` starts Vite preview, launches Electron, starts the local .NET API through the Electron main process, runs a simulated runtime session from the rendered UI, verifies SignalR station/timeline events plus trace row output, opens the Trace workbench to load search results, details, and export data, then opens the Processes workbench, verifies the official Blockly workspace, registers a custom Blockly block, adds a Command node, creates/publishes a Blockly-default PythonScript process definition, creates a matching engineering configuration snapshot, starts the published process from the UI, publishes a second configuration snapshot through the Engineering workbench, registers/connects a device through the Devices workbench, and starts/stops the sample plugin through the Plugins workbench.
