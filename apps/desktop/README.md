# OpenLineOps Desktop

Electron IDE shell for authoring and operating portable OpenLineOps automation projects.

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

## Product Boundary

- The renderer has no Node.js access; backend lifecycle and HTTP access cross the preload `contextBridge`.
- Authoring APIs are always scoped to one Automation Project and one portable Application.
- Blockly workspaces compile server-side directly to immutable Flow IR v2 actions. Python is a separate, explicit action type and cannot return a runtime execution plan.
- Runtime starts only from a published project release snapshot. There is no simulated-session endpoint or direct ProcessDefinition launch path.
- Runtime commands, steps, monitoring, and trace records carry required ActionId, TargetKind, and TargetId identities.
- SignalR monitoring connects at `/hubs/runtime-progress`.
- The API base URL defaults to `http://localhost:5135` and can be overridden with `OPENLINEOPS_API_BASE_URL`.
- Backend project and repository roots can be overridden with `OPENLINEOPS_API_PROJECT` and `OPENLINEOPS_REPO_ROOT`.

## Current Workbenches

- Start window for creating, opening, importing, and reopening `.oloproj` projects.
- Project explorer for independently portable `.oloapp` Applications.
- Flow Designer for distinct Blockly, PythonScript, Command, Decision, Delay, Start, and End nodes.
- Application-scoped declarative Blockly block catalog and registration.
- Engineering, devices, plugins, traceability, monitoring, and alarm workbenches.
- Line Designer for Application-owned production-line definitions, DUT identity, workstations, ordered stages, and external test adapters.
- Release publication and release-snapshot runtime launch.

## Packaging

`npm run package:win` builds the renderer and Electron main/preload code, then creates an unsigned Windows development package under `release/desktop/win-unpacked`. The .NET API is a separate release artifact selected with `OPENLINEOPS_REPO_ROOT` or `OPENLINEOPS_API_PROJECT`.

## End-to-End Smoke Test

`npm run smoke:e2e` launches the real Electron application and local API, opens a portable project/Application, authors and publishes a Blockly flow, publishes a project release, starts that immutable release, verifies runtime state, then creates and saves a production-line definition through Line Designer. The smoke path does not use removed global Process APIs or development runtime-start endpoints.
