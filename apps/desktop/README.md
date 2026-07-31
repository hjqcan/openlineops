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
npm run smoke:e2e:packaged-existing
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
- Studio spawns its API on an operating-system-assigned loopback port and publishes the origin only after a protected-file, PID, and per-launch nonce proof succeeds. Ambient API URL or port settings are not trusted.
- Studio holds a per-user-data single-instance lock before credentials or runtime state are touched. The API holds an operating-system handle to that exact Electron parent and shuts itself down if the parent is killed.
- Studio provisions separate Engineering/Operator and Safety root credentials inside its protected user-data security directory, then derives disposable process-session credentials for each API launch. For an externally provisioned identity, set both `OPENLINEOPS_API_TOKEN_FILE` and `OPENLINEOPS_API_SAFETY_TOKEN_FILE` to absolute paths, plus the optional canonical `OPENLINEOPS_API_ACTOR_ID`; both files and their parent directories must already exist with private ACLs. External mode performs no filesystem writes: Studio never creates or repairs externally managed credential paths, and insecure or partial token files stop startup.
- Packaged mutable databases, Trace artifacts, and external-program workspaces live under one `data/runtime-state` root. Its marker binds the state to the strict physical-file inventory and SHA-256 digest of the complete packaged desktop/API/ScriptWorker/PluginHost content. Electron validates that inventory through `original-fs`, so ASAR virtual entries cannot change its meaning. A mismatch uses a two-phase activation: the prior root remains recoverable until the new API proves healthy and package bytes are reverified; credentials and Station packages are outside that root and are not migrated or reset.
- Source-development binaries are loaded from the active `OPENLINEOPS_REPO_ROOT`; build the .NET solution before starting Studio. Packaged builds never depend on a source checkout.

## Current Workbenches

- Start window for creating, opening, importing, and reopening `.oloproj` projects.
- Project explorer for independently portable `.oloapp` Applications.
- Flow Designer for distinct Blockly, PythonScript, Command, Decision, Delay, Start, and End nodes.
- Application-scoped declarative Blockly block catalog and registration.
- Engineering, devices, plugins, traceability, monitoring, and alarm workbenches.
- Line Designer for Application-owned Product Models, Station-bound Operations, typed route graphs, and portable external-program resources.
- Line Operations for Active Runs, dual execution/judgement axes, product queues, Slot occupancy, and operator control commands.
- Production Trace separates immutable Run evidence from the latest Product Material Lifecycle, and visibly marks unload or transfer records that occurred after Run completion.
- Release publication and release-snapshot runtime launch.

## Packaging

`npm run package:win` builds the renderer and Electron main/preload code, publishes self-contained Windows API, ScriptWorker, and PluginHost runtimes, then creates an unsigned Windows package under `release/desktop/win-unpacked`. Extensions are imported explicitly into a Project Application and no sample or global plugin directory is bundled. The packaged IDE starts its own API automatically and stores writable runtime databases under the current user's bound `data/runtime-state`; it does not require a source checkout or separately installed .NET runtime.

## End-to-End Smoke Test

`npm run smoke:e2e` launches the real Electron application and local API, opens a portable project/Application, authors and publishes a Blockly flow, publishes a project release, starts that immutable release, verifies runtime state, then creates and saves a production-line definition through Line Designer. The smoke path does not use removed global Process APIs or development runtime-start endpoints.

`npm run smoke:e2e:packaged` repeats the same workflow through the generated `OpenLineOps.exe` and its bundled API runtime, so packaging and startup-path regressions fail the build.

`npm run smoke:e2e:packaged-existing` tests the already staged package without rebuilding it. It proves incompatible-state replacement, same-package restart persistence, single-instance exclusion, and parent-death API cleanup against the exact release candidate.
