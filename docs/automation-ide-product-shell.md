# OpenLineOps Automation IDE And Runner Design

Last updated: 2026-07-10

## Decision Summary

OpenLineOps is an IDE for engineering, validating, publishing, and operating
automation and test lines.

The product is split into distinct surfaces that share application services but
do not share UI state or a mutable in-memory project object:

1. **OpenLineOps Studio** is the IDE. It creates, opens, edits, validates,
   publishes, debugs, and traces projects.
2. **OpenLineOps Runner** loads and operates a published project release without
   opening the IDE. It can provide a focused operator UI.
3. **OpenLineOps Agent** is the unattended Windows Service or remote station
   host. It accepts verified releases and durable run requests.
4. **OpenLineOps CLI** packages, verifies, deploys, and runs releases for local
   automation and CI/CD.

The IDE and every non-IDE host execute the same immutable project release
through the same `IProjectRunService`. No host is allowed to execute mutable
draft state or reconstruct a trusted execution plan from UI input.

## SmartMatriX Reference Boundary

The existing SmartMatriX implementation is a reference, not a compatibility
target.

Useful product ideas:

- One directory represents one automation project.
- The start experience is New, Open, and Recent Project.
- The project explorer uses automation language such as App, System, Driver,
  Station, Group, and Slot.
- The runtime has explicit Reset, Connect, Initialize, Run, Pause, Continue,
  Stop, Emergency Stop, and Shutdown operations.
- A project can define an operator-facing run layout.

Ideas that must not be copied:

- One full IDE executable serving as both Studio and Runner.
- A global mutable Kernel as the active project.
- An `EditMode` flag that only hides IDE windows.
- Loading the same project into multiple independent object graphs.
- Serializing a complete CLR object graph as the project format.
- Running the mutable editing state directly.
- Writing runtime databases, recovery state, and logs back into source project
  files.

## Product Mental Model

The user works with four different artifacts. They must remain explicit in the
UI, APIs, and domain language.

```text
Editable Project Folder
        |
        | Publish and validate
        v
Immutable ProjectRelease
        |
        | Package and sign
        v
Deployable .olopkg
        |
        | Submit to a station
        v
RuntimeSession
```

### Editable Project Folder

The version-controllable engineering source opened by Studio. It contains
applications, topology, layouts, flows, Blockly workspaces, Python sources,
configuration, binding requirements, and project settings.

It is allowed to be incomplete and invalid while the user is editing it. A
Runner never executes it directly.

### ProjectRelease

An immutable, fully resolved revision created by a server-side publisher. It
freezes every input required for deterministic execution and records the source
revision from which it was produced.

An IDE debug run also uses an immutable release. Studio may create an ephemeral
development release, but Runtime still never reads mutable editor state.

### `.olopkg`

A signed, content-addressed deployment package containing one or more verified
entrypoints. It is safe to copy to another station and does not depend on an IDE
database.

### RuntimeSession

Live operational state created from one release digest. Runtime data is stored
in a station instance-data directory, not in the project source directory.

## Studio Lifecycle

### Start Center

When no project is open, Studio shows only:

- New Project
- Open Project Folder
- Recent Projects
- Pinned Projects
- Runtime connection diagnostics in the status bar

Engineering, Devices, Flows, and Run tools remain unavailable until a project
session exists.

### Project Session

Opening a project creates a `ProjectSession` with explicit state:

```text
activeProjectId
activeApplicationId
openEditorTabs
activeEditorTabId
selection
dirtyResourceIds
workspaceMode
runPhase
activeReleaseId
activeRuntimeSessionId
```

`activeApplicationId` is required. Code must not silently use
`applications[0]`.

### Edit Mode

Edit mode exposes topology, site layout, configuration, device binding, Blockly
flow, Python, block catalog, and trace-navigation editors.

Saving persists project source. Publishing performs cross-context validation
and produces a release. Save and Publish are separate operations.

### Run Mode

Run mode is an operational workspace, not another dashboard page. Its state
machine is:

```text
Idle -> Preparing -> Starting -> Running -> Stopping -> Completed
                                      \-> Failed
                                      \-> RecoveryRequired
```

Before a run:

- If no release exists, Studio offers `Publish and Run` or Cancel.
- If the project is dirty, Studio offers `Publish and Run`, `Run Last
  Published`, or Cancel.
- A Run Profile supplies serial, batch, fixture, device, actor, station, and
  environment-specific inputs.
- Preflight verifies package digest, device availability, secrets, station
  lease, safety policy, and plugin compatibility.

During a run the current project session is read-only by default. A future
multi-revision workflow may allow editing the next draft in a separate session,
but it cannot mutate the active release.

Backend process Start/Stop is a diagnostic concern. It must not be presented as
the line's Run/Stop control.

## Studio Information Architecture

```text
No project
+--------------------------------------------------------------+
| Title / runtime connection                                   |
+--------------------------------------------------------------+
| Activity | Start Center                                      |
| Bar      | New | Open | Recent | Pinned                      |
+--------------------------------------------------------------+
| Status: runtime, API, version                                |
+--------------------------------------------------------------+

Project open
+--------------------------------------------------------------+
| Project / Application / Editor       Edit | Run   Run Project |
+------+----------------+--------------------------+-------------+
| Act. | Project        | Editor Tabs              | Inspector   |
| Bar  | Explorer       +--------------------------+             |
|      | Applications   | Layout / Flow / Python   | Selection   |
|      | Systems        | Configuration / Trace    | Properties  |
|      | Drivers        +--------------------------+-------------+
|      | Groups / Slots | Problems | Output | Runtime | Trace    |
+------+----------------+----------------------------------------+
| Project | Application | Dirty/Published | Release | Devices    |
+--------------------------------------------------------------+
```

Rules:

- Activity Bar changes the tool/sidebar view; it does not replace editor tabs.
- Project Explorer is a projection of project resources, not the persistence
  model itself.
- Topology and Site Layout use the full central editor area.
- Blockly and Python are two views of the same flow document.
- Inspector displays the selected Node, Module, Driver Binding, Group, Slot,
  Flow Node, Block, or Layout Element.
- Generated Python, workspace JSON, validation errors, runtime output, and trace
  details belong in the Bottom Panel.
- Block Catalog belongs in an Explorer/Extensions view, not permanently beside
  the Blockly canvas.
- Run Profile and runtime results belong in Run Mode, not in flow properties.

## Project Source Shape

The editable project remains text-based and reviewable:

```text
project-root/
  openlineops.project.json
  applications/
    application-main--<stable-hash>/
      application.json
      topology/
        topology-main--<stable-hash>.json
      layouts/
        layout-main--<stable-hash>.json
      flows/
        inspect-part--<stable-hash>/
          flow.json
          flow.ir.json
          nodes/
            inspect--<stable-hash>/
              workspace.<sha256>.blockly.json
              generated.<sha256>.py
            report--<stable-hash>/
              source.<sha256>.py
      bindings/
        driver-bindings.json
      blocks/
        custom/
  scripts/
    project_helpers.py
  configuration/
    recipes/
    station-profiles/
    run-profiles/
  blocks/
    custom/
  releases/
    active-release.json
    index.json
```

The manifest stores resource paths, schema versions, source revision, active
application, and active release pointer. It must not be the only file containing
project data.

Save operations use staging files followed by atomic replacement. Every schema
has an explicit version and migration path.

For flow source, content-addressed Blockly and Python files are written first;
`flow.json` is atomically replaced last and acts as the commit pointer. It
records the artifact paths and hashes, so an interrupted save retains the prior
complete flow and manual file tampering is detected on open.

Physical resource keys use a readable slug plus a stable hash. User-provided
ids are data inside the document and are never used as unchecked path segments.
All resource paths stay relative to the opened project root, so moving the
folder does not change the project identity.

## Release Publishing

The UI submits intent such as application and entrypoint. It does not submit a
supposedly trusted list of resolved bindings or block versions.

`ProjectReleasePublisher` performs the publish use case:

1. Load the project source revision.
2. Load published flow, configuration, topology, block, device, and plugin
   artifacts through application ports.
3. Validate graph structure, target references, capability compatibility,
   safety/interlock policy, Python policy, source hashes, and plugin trust.
4. Resolve every capability, target, command, device instance, command
   definition, plugin id, and plugin version.
5. Materialize a complete release in a staging directory.
6. Generate hashes and signature metadata.
7. Verify the staged release using the same verifier as Runner.
8. Atomically move the release into place and update the active-release pointer.

If any step fails, neither the project manifest nor active release changes.

## `.olopkg` Contents

A deployable package contains at least:

- package schema, project/application/release/snapshot identity, entrypoints,
  source revision, minimum/maximum Runner version, RID/ABI, and policy profile;
- resolved, versioned Flow IR including nodes, transitions, loops, timeouts,
  cancellation, interlocks, and trace mapping;
- Python source, version, hash, and execution policy;
- Blockly workspace and block definitions when authoring audit/reopen support is
  required;
- complete recipe values and station configuration;
- resolved capability/target/command to provider/device/plugin route table;
- topology, modules, groups, slots, and layout needed by Runner/operator UI;
- required driver/plugin packages and dependency lock;
- references to secrets, never secret values;
- `checksums.sha256` and package signature.

Runner extracts packages read-only into a content-addressed cache and verifies
path traversal, size limits, hashes, signatures, compatibility, and policy
before loading any executable component.

## Shared Execution Use Case

Add a host-neutral cross-context application module:

```text
modules/OpenLineOps.ProjectExecution.Application/
  IProjectRunService
    SubmitAsync
    GetAsync
    StopAsync
    EmergencyStopAsync
  IProjectReleaseLoader
  IProjectReleaseVerifier
  IExecutionPreflight
  IStationRunLease
  IRunRequestRepository
```

Adapters:

```text
Studio Run command ---------+
HTTP API controller --------+--> IProjectRunService --> durable worker --> Runtime
CLI adapter ----------------+
Windows Service named pipe -+
Remote Agent endpoint ------+
```

The run request is durably stored before hardware is touched. A station lease
and idempotency key prevent accidental duplicate runs.

The current synchronous HTTP request model must evolve to submit-and-observe:

- Studio, Service, and Agent receive a session id immediately.
- CLI submits and then waits or streams events.
- Runtime publishes progress independently of the caller connection.

## Runner And CLI

Suggested commands:

```powershell
openlineops package C:\Projects\LineA `
  --application main --snapshot active `
  --output LineA-1.2.0.olopkg

openlineops verify LineA-1.2.0.olopkg --json

openlineops run LineA-1.2.0.olopkg `
  --entry main `
  --input run.json `
  --idempotency-key order-20260710-001 `
  --json --result result.json

openlineops run C:\Projects\LineA --snapshot active
```

Running a project directory means loading its already-materialized active
release. Runner rejects a directory containing only draft source.

Suggested exit codes:

- `0`: Completed
- `1`: Failed, Rejected, TimedOut, or Canceled terminal run
- `2`: command/input usage error
- `3`: invalid package, hash, signature, schema, or compatibility
- `4`: preflight, secret, safety, or device unavailable
- `5`: station busy or idempotency conflict
- `6`: RecoveryRequired
- `70`: internal Runner fault
- `130`: interrupted by Ctrl+C

Runner stores mutable state under `%ProgramData%\OpenLineOps\runner`, separated
into package cache, state database, run evidence, and logs. It never writes into
the source project or signed package.

## Operator Runtime Lifecycle

The lifecycle adapts the useful part of SmartMatriX's execution service while
removing global Kernel coupling:

```text
LoadRelease
Verify
AcquireStationLease
Reset
Connect
Initialize
Run
Pause / Continue
Stop / EmergencyStop
Shutdown
ReleaseStationLease
```

Every transition is a command with authorization, idempotency, timeout, audit,
and terminal result. Emergency Stop is independent of normal flow execution.

## Recovery And Safety

The first Runner release must not pretend it can safely resume arbitrary
hardware actions. An interrupted non-terminal session becomes
`RecoveryRequired`; Runner performs provider safe-stop, keeps the station lease,
and waits for an authorized operator decision.

True resume requires persisted flow cursor, traversal counts, command
idempotency, provider state queries, checkpoint policies, and safe re-entry
rules.

Production policy requires:

- signed project and plugin packages;
- process-isolated Python with least privilege or container isolation;
- local Service control over an ACL-protected named pipe;
- mTLS, signed commands, and nonce protection for remote Agent control;
- immutable event evidence and default log redaction;
- explicit Simulator profiles with no silent real-device fallback.

## Migration Plan

### Phase 0: Product Shell

- Replace Dashboard-first navigation with Start Center and Project Session.
- Add Activity Bar, Project Explorer, Editor Area, Bottom Panel, and Status Bar.
- Separate Edit/Run mode from backend process diagnostics.
- Add explicit active application selection.

### Phase 1: Durable Project Source

- Persist Projects, Topology, Layout, Processes, Blocks, and Configuration by
  project/application scope.
- Split the single manifest into a text project package.
- Add resource revisions, dirty state, migrations, and atomic save.

### Phase 2: Release Publisher

- Add `ProjectReleasePublisher` and cross-context validation.
- Freeze resolved Flow IR, Python, configuration, binding table, topology, and
  plugin lock.
- Atomically update the active release.
- Make Studio Run consume the same release loader as Runner.

### Phase 3: One-Shot Runner

- Add package/verify/run CLI.
- Use durable SQLite runtime state and station lease.
- Enforce production Python and plugin trust policy.

### Phase 4: Service And Operator UI

- Add Windows Service with named-pipe control.
- Add focused Runner operator UI that never loads IDE editors.
- Add Pause, Continue, Stop, Emergency Stop, and safe shutdown.

### Phase 5: Agent And Recovery

- Add signed deployment, mTLS remote control, and offline result buffering.
- Add explicit checkpoint/resume only after device and command recovery
  contracts exist.

## Acceptance Criteria

The redesign is complete when:

1. Studio launches into New/Open/Recent without showing unrelated dashboards.
2. Opening a project restores its scoped applications and all editor resources
   after process restart.
3. Layout, Blockly, Python, configuration, and device bindings open as editor
   tabs under one project session.
4. Save never publishes, and Publish never trusts client-resolved execution
   data.
5. IDE Run and CLI/Service Run load the same verified release digest.
6. `openlineops run LineA.olopkg` succeeds on a station where Studio is not
   installed.
7. Runtime logs, state, and recovery data never modify project source.
8. A failed package verification or unavailable device prevents hardware
   access and returns a deterministic error/exit code.
