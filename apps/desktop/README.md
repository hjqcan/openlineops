# OpenLineOps Desktop

Electron IDE shell for authoring and operating portable OpenLineOps automation projects.

## Commands

```powershell
npm install
npm run typecheck
npm run test:production-route-validation
npm run build
npm run package:win
npm run smoke:e2e
npm run smoke:e2e:packaged
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
- Blockly workspaces compile server-side directly to immutable Flow IR actions. Python is a separate, explicit action type and cannot return a runtime execution plan.
- Runtime starts only from a published project release snapshot. There is no simulated-session endpoint or direct ProcessDefinition launch path.
- Runtime commands, steps, monitoring, and trace records carry required ActionId, TargetKind, and TargetId identities.
- Operations reconnects through the persisted Active Runs and Line State projections; line, Station, and Slot filters are Coordinator queries.
- SignalR monitoring connects at `/hubs/runtime-progress`.
- The API base URL defaults to `http://localhost:5135` and can be overridden with `OPENLINEOPS_API_BASE_URL`.
- Source-development backend roots can be overridden with `OPENLINEOPS_API_PROJECT` and `OPENLINEOPS_REPO_ROOT`; packaged builds never depend on a source checkout.

## Current Workbenches

- Start window for creating, opening, importing, and reopening `.oloproj` projects.
- Project explorer for independently portable `.oloapp` Applications.
- Flow Designer for distinct Blockly, PythonScript, Command, Decision, Delay, Start, and End nodes.
- Application-scoped declarative Blockly block catalog and registration.
- Engineering, devices, plugins, traceability, monitoring, and alarm workbenches.
- Line Designer for Application-owned Product Models, Station-bound Operations, typed route graphs, and portable external-program resources.
- Line Operations for Active Runs, dual execution/judgement axes, product queues, Slot occupancy, and operator control commands.
- Release publication and release-snapshot runtime launch.

## Packaging

`npm run package:win` builds the renderer and Electron main/preload code, publishes a self-contained Windows API runtime and bundled sample plugin, then creates an unsigned Windows package under `release/desktop/win-unpacked`. The packaged IDE starts its own API automatically and stores writable runtime databases under the current user profile; it does not require a source checkout or separately installed .NET runtime.

## End-to-End Smoke Test

`npm run smoke:e2e` launches the real Electron application and local API, opens a portable project/Application, authors and publishes a Blockly flow, publishes a project release, starts that immutable release, verifies runtime state, then creates and saves a production-line definition through Line Designer. The smoke path does not use removed global Process APIs or development runtime-start endpoints.

`npm run smoke:e2e:packaged` repeats the same workflow through the generated `OpenLineOps.exe` and its bundled API runtime, so packaging and startup-path regressions fail the build.
