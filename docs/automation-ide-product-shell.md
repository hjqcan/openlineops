# OpenLineOps Automation IDE And Runner Design

Last updated: 2026-07-10

## Decision Summary

OpenLineOps is an IDE for engineering, validating, publishing, and operating
automation and test lines.

The target product is split into distinct surfaces that share application
services but do not share UI state or a mutable in-memory project object:

1. **OpenLineOps Studio** is the IDE. It creates, opens, edits, validates,
   publishes, debugs, and traces projects.
2. **OpenLineOps Runner** loads and operates a published project release without
   opening the IDE. It can provide a focused operator UI.
3. **OpenLineOps Agent** is the unattended Windows Service or remote station
   host. It accepts verified releases and durable run requests.
4. **OpenLineOps CLI** packages, verifies, deploys, and runs releases for local
   automation and CI/CD.

Today, Studio can publish an immutable application release and the Project
Snapshot API path executes its frozen Flow IR without reading editable project
source. A one-shot `OpenLineOps.Runner` can execute the same kind of Project
Snapshot headlessly from a project directory. Agent, service/queue hosting,
package/deploy/verify CLI commands, deployable package signing, and a shared
host-neutral `IProjectRunService` remain target architecture. The boundary is
that no production host may execute mutable draft state or reconstruct trusted
execution data from UI input.

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
- Serializing a complete CLR object graph as the project format. SmartMatriX
  `.ak` is prior-art reference only; it is not accepted OpenLineOps project
  input or source format.
- Running the mutable editing state directly.
- Writing runtime databases, recovery state, and logs back into source project
  files.

## Product Mental Model

The user works with four different artifacts. They must remain explicit in the
UI, APIs, and domain language.

```text
Editable Project Folder
        |
        | Publish and validate (implemented)
        v
Immutable ProjectRelease
        |\
        | \ Local Project Snapshot run (Studio/one-shot Runner, implemented)
        |  -----------------------------------------------> RuntimeSession
        |
        | Package and sign (future)
        v
Deployable .olopkg -- submit to station (future) ---------> RuntimeSession
```

### Editable Project Folder

The version-controllable engineering source opened by Studio. It contains
one root `<projectId>.oloproj` and independently movable application directories.
Each application directory has its own `.oloapp`, topology, layouts, flows,
Blockly workspaces, Python sources, configuration, binding requirements,
scripts, and custom blocks. The root project composes applications by relative
reference.

It is allowed to be incomplete and invalid while the user is editing it. A
Runner never executes it directly.

### ProjectRelease

The current implementation is an immutable application-source artifact created
by a server-side publisher. Its manifest records server-resolved execution
metadata, per-file hashes, a release content digest, and canonical versioned
Flow IR. The Project Snapshot runtime verifies that Flow IR and reads process
validation source, configuration, and device bindings only from the frozen
release. A complete provider/plugin package lock is still needed before this is
the fully resolved deployment revision in the target architecture.

The production direction is that IDE runs also use immutable releases. The
implemented Project Snapshot launch already enforces this boundary; ephemeral
development releases are a future option.

### `.olopkg`

A future signed, content-addressed deployment package containing one or more
verified entrypoints. The package format, signing, verification, and deployment
tooling are not implemented yet.

### RuntimeSession

Live operational state created from one release digest. The current one-shot
path uses the configured Runtime persistence and does not write session state
into editable application source. A dedicated station instance-data directory
and durable recovery database belong to the future service Runner.

## Studio Lifecycle

### Start Center

When no project is open, Studio now focuses on:

- searchable recent projects grouped by recency;
- Create Project;
- Open Project (`.oloproj`) or Open Project Folder;
- keyboard-safe Create/Open dialogs and recent-project activation.

This is a VS-inspired but independently designed OpenLineOps Start Center. The
Activity Bar, explorer, editors, and runtime tools are not shown until a project
session exists. Opening a project transitions into the automation IDE workbench
with explicit project and application selection.

### Project Session

Opening a project establishes explicit active project and application state.
The fuller target `ProjectSession` state remains:

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

`activeProjectId` and `activeApplicationId` are implemented boundaries;
editor-tab and dirty-resource state still need further work. Code must not
silently use `applications[0]`.

Adding an existing application is an explicit composition operation. The user
copies the complete application directory under the open project's
`applications` directory and imports its `.oloapp`; Studio then adds the
project-relative reference without rewriting the application's internal files.

### Edit Mode

Edit mode exposes topology, site layout, configuration, device binding, Blockly
flow, Python, block catalog, and trace-navigation editors.

Saving persists project source. Publishing performs cross-context validation
and produces a release. Save and Publish are separate operations.

### Run Mode

The target Run mode is an operational workspace, not another dashboard page.
Its state machine is:

```text
Idle -> Preparing -> Starting -> Running -> Stopping -> Completed
                                      \-> Failed
                                      \-> RecoveryRequired
```

The implemented Project Snapshot launch already provides immutable input. The
following preflight and full operator state behavior are still target behavior:

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
| OpenLineOps Studio                                           |
+--------------------------------------------------------------+
| Start Center                                                 |
| Search Recent Projects             Create | Open Folder      |
+--------------------------------------------------------------+
| Recent: Today | This Week | Older                            |
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
  <project-id>.oloproj
  applications/
    <application-directory>/
      <application-name>.oloapp
      topology/
        topology-main--<stable-hash>.json
      layouts/
        layout-main--<stable-hash>.json
      flows/
        inspect-part--<stable-hash>/
          flow.json
          nodes/
            inspect--<stable-hash>/
              workspace.<sha256>.blockly.json
              generated.<sha256>.py
            report--<stable-hash>/
              source.<sha256>.py
      configuration/
        workspaces/
          workspace-main--<stable-hash>.json
        projects/
          project-main--<stable-hash>.json
        recipes/
          recipe-default--<stable-hash>.json
        station-profiles/
          station-profile-local--<stable-hash>.json
      bindings/
        driver-bindings.json
      blocks/
        custom/
          block-user-inspect--<stable-hash>/
            versions/
              version-000001.json
      scripts/
        application_helpers.py
  releases/
    release-<safe-snapshot-id>/
      release.json
      source/
        applications/
          <application-folder>/
            <name>.oloapp
            ...frozen application source...
```

The `.oloproj` is the IDE entrypoint for one automation project. It stores
project identity and settings, project-relative `.oloapp` references, snapshot
publication history, and the selected snapshot id. Each immutable snapshot
records its release manifest path and digest; there is no separately managed
deployment-ready active-release pointer yet.

Each `.oloapp` stores application identity, display metadata, and relative links
to application-local resources. Every file below the application root retains
`ApplicationId` and its own resource identity but must not persist the host
`ProjectId`. This is the portability boundary: copy the complete directory into
another project's `applications` directory, import the `.oloapp`, and use it
without recursively rewriting topology, layouts, flows, Blockly/Python,
configuration, bindings, scripts, or custom blocks.

The current `.oloproj` is a single automation project, not a multi-project
solution, so it is intentionally not named `.olosln`. A future solution layer
can compose multiple `.oloproj` files without changing this application
boundary. Obsolete project filenames and schema versions are rejected; there is
no compatibility reader or automatic migration branch.

Save operations use staging files followed by atomic replacement. Every schema
has one exact accepted version. A format change is an explicit breaking cutover.

For flow source, content-addressed Blockly and Python files are written first;
`flow.json` is atomically replaced last and acts as the commit pointer. It
records the artifact paths and hashes, so an interrupted save retains the prior
complete flow and manual file tampering is detected on open.

Engineering workspaces, engineering projects and their configuration snapshots,
recipes, station profiles, and custom Blockly block histories are application-
local source. Repositories receive explicit project/application scope to resolve
the application root, but persisted application-local documents do not retain
the host project identity. Moving a project folder, copying an application to a
different project, or reusing a local id in another application therefore does
not redirect the resource to a global database. Engineering JSON is
schema-versioned and atomically replaced; each custom block version is an
immutable `version-000001.json`-style artifact.

Release manifest schema v3 records the exact Application project path plus the
selected topology, layouts, process and version, Engineering configuration
snapshot, capability bindings, target
references, publish-selected Blockly block versions, canonical Flow IR schema,
JSON and SHA-256, every copied file's size and SHA-256, and a content digest.
The block version is currently the catalog version selected at publish time for
each block type found in the workspace. Exact workspace block-version/contract
locks, a complete provider/plugin dependency lock, package signatures, and an
active-release pointer are not implemented. They remain future publisher and
deployment outputs, never trusted editable-source inputs.

Physical resource keys use a readable slug plus a stable hash. User-provided
ids are data inside the document and are never used as unchecked path segments.
All application references stay relative to the opened project root and all
application resource links stay relative to the application root. Import
rejects traversal, reparse-point escapes, duplicate identity, and path
collisions; the `.oloapp` must already be inside the target project's
`applications` directory.

## Release Publishing

The UI submits publication intent: snapshot, application, process definition,
and configuration snapshot ids. It does not submit a supposedly trusted list
of resolved bindings, targets, layouts, or block versions.

The implemented `ProjectReleasePublisher` performs this use case:

1. Resolve the explicitly scoped project and application source.
2. Require a linked topology, at least one layout, a published process, and one
   matching published Engineering configuration snapshot.
3. Validate that every capability required by the process is declared by the
   topology and has both a topology driver binding and configuration device
   binding.
4. Resolve topology targets and bindings plus the current catalog version for
   every Blockly block type found in the process workspace.
5. Compile the published process graph to canonical
   `openlineops.flow-ir/v1`, then record its schema, JSON, and SHA-256.
6. Copy the complete application source into a staging release, write per-file
   size and SHA-256 records, and compute the release content digest.
7. Verify the staged release, atomically move it into its immutable final
   directory, and refuse overwrite of an existing snapshot release.
8. Resolve and compile the copied source again and require the same semantic
   release metadata and Flow IR before recording the project-relative manifest
   path and digest in the Project Snapshot.

Opening a release validates schema, scope and snapshot identity, safe paths,
the exact file set, sizes, file hashes, and content digest. The Project Snapshot
runtime launcher additionally compares release metadata with the snapshot and
verifies the frozen Flow IR's canonical form, hash, and process identity. It
loads process validation source and Engineering configuration only from the
frozen source root and maps that Flow IR into the executable runtime process.
Release-identified device commands resolve the topology capability and device
binding from that same release, with no mutable/global configuration fallback.

This is a local immutable release boundary. It does not yet create an
`.olopkg`, sign a package, lock plugin binaries, update an active-release
pointer, or provide package/deployment verification. The current one-shot
Runner consumes this local release boundary; it is not an `.olopkg` verifier.

The development-only `POST /api/runtime/sessions/simulated` and
`POST /api/process-definitions/{id}/runtime-sessions` endpoints are isolated as
Development/Test diagnostics. They return `403` unless the host
environment is `Development` or `Test` and
`OpenLineOps:Runtime:DevelopmentStarts:Enabled=true` is explicitly enabled.
Project Snapshot launch is the production execution route.

## Frozen Flow IR And Dynamic Child Actions

Flow IR v1 freezes process/node/transition identity, command targets, timeouts,
loop metadata, Python source hashes, and a dynamic action slot for PythonScript
nodes. Current Blockly authoring still produces Python that returns an
`automation_plan`, so Blockly and manual-Python children are resolved at
runtime with container-level source mapping.

Dynamic expansion now participates in the `RuntimeSession` aggregate rather
than recursively bypassing it. The outer action and every child step/command
carry `ActionId`; the child step records its parent and sequence, and execution
context propagates that identity. The runner persists lifecycle changes before
and after execution. Child failure or rejection fails the container, external
cancellation cancels it, and child or container timeout propagates as the
appropriate terminal outcome. Runtime and trace projections can therefore
distinguish and order nested actions.

This is not yet static Blockly compilation. Block id/type/version source maps,
compile-time field validation, and exact block/contract locks remain future
Flow IR inputs.

## Declarative Runtime Action Contract Foundation

Processes.Application now defines strict
`openlineops.runtime-action-contract/v1` typed contracts and deterministic
canonical serialization/SHA-256. The five built-in Blockly blocks expose safe
declarative contracts for `deviceCommand`, `delay`, and `resultPatch`, using
literal, field, context, object, and array values. Capability and command are
fixed contract data; retry is currently zero. The validator rejects unknown or
duplicate properties, dynamic routing, scripts/templates/raw expressions,
non-finite numbers, hostile null structures, and non-canonical documents.

This D1 work is a metadata and validation foundation only. Persisted custom and
plugin-generated blocks still use `LegacyPythonTemplate`; the workspace does
not yet store an exact definition version and contract hash; Publisher does not
yet compile individual Blockly blocks to static Flow IR children; and plugin
package/version/hash locks are not frozen. Generated Python and dynamic action
expansion remain the current execution path.

## Future `.olopkg` Contents

A future deployable package should contain at least:

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

A future package-capable Runner should extract `.olopkg` files read-only into a
content-addressed cache and verify path traversal, size limits, hashes,
signatures, compatibility, and policy before loading any executable component.

## Future Shared Execution Use Case

The headless product should add a host-neutral cross-context application module:

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

`src/OpenLineOps.Runner` is an implemented one-shot headless executable. It
opens a project directory or `<projectId>.oloproj`, selects an explicit or active
Project Snapshot, requires an immutable release descriptor, runs it through
`ProjectReleaseRuntimeSessionLauncher`, writes one JSON result using Runner
output schema v1 to standard output, and exits after the session reaches a
terminal state. All non-`.oloproj` project formats are rejected.

Current command:

```powershell
dotnet run --project src/OpenLineOps.Runner/OpenLineOps.Runner.csproj -- `
  run C:\Projects\LineA --snapshot active `
  --serial SN-001 --batch BATCH-001 --fixture fixture-a `
  --device device-a --actor operator-a
```

`--snapshot` defaults to `active`; the remaining options are optional runtime
trace inputs. Runner rejects draft-only projects and snapshots without a
release. Runtime configuration comes from `appsettings.json`,
`appsettings.<environment>.json`, and environment variables.

Implemented stable exit codes:

- `0`: completed successfully, or help displayed;
- `2`: command-line usage error;
- `3`: project path or manifest could not be opened;
- `4`: requested or active snapshot could not be selected;
- `5`: selected snapshot has no immutable release;
- `6`: immutable release verification or runtime start was rejected;
- `7`: runtime session ended in a non-Completed terminal state;
- `8`: execution was canceled;
- `70`: unexpected configuration/internal error.

This Runner is synchronous and one-shot. It is not a daemon, Windows Service,
queue, remote Agent, recovery host, operator UI, or station-lease manager. It
does not package, sign, verify, cache, or deploy `.olopkg` files, and it does not
provide durable resume/idempotency across process termination. Those
capabilities require the future host-neutral run service and durable runtime
state boundary.

Future package/deployment CLI commands remain a design target:

```powershell
openlineops package C:\Projects\LineA --snapshot active --output LineA.olopkg
openlineops verify LineA.olopkg --json
openlineops deploy LineA.olopkg --station line-a
```

## Operator Runtime Lifecycle

The following service/operator lifecycle remains target behavior. The current
one-shot Runner performs load, release verification, run, and terminal JSON
reporting only. The fuller lifecycle adapts the useful part of SmartMatriX's
execution service while removing global Kernel coupling:

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

The current one-shot Runner does not provide durable recovery and must not be
described as safely resuming arbitrary hardware actions. In the future service
model, an interrupted non-terminal session becomes `RecoveryRequired`; the
service performs provider safe-stop, keeps the station lease, and waits for an
authorized operator decision.

True resume requires persisted flow cursor, traversal counts, command
idempotency, provider state queries, checkpoint policies, and safe re-entry
rules.

Future production policy requires:

- signed project and plugin packages;
- process-isolated Python with least privilege or container isolation;
- local Service control over an ACL-protected named pipe;
- mTLS, signed commands, and nonce protection for remote Agent control;
- immutable event evidence and default log redaction;
- explicit Simulator profiles with no silent real-device fallback.

## Delivery Plan

### Phase 0: Product Shell

- Implemented: replace Dashboard-first entry with the New/Open/Recent Start
  Center and transition to the project workbench only after open.
- Implemented: explicit active project/application selection, Activity Bar,
  explorer/editor workbench, bottom-panel surfaces, and status messaging.
- Remaining: richer editor tabs, dirty-resource workflow, topology editing, and
  clearer complete Edit/Run operator state behavior.

### Phase 1: Durable Project Source

- Implemented: persist Projects, Topology, Layout, Processes, Blockly/Python,
  Blocks, and Engineering Configuration by explicit project/application scope.
- Implemented: text project-folder source, content-addressed flow artifacts,
  schema-versioned resources, immutable custom-block versions, and atomic save.
- Implemented: root `.oloproj` composition plus independent `.oloapp`
  application directories whose internal files do not persist host `ProjectId`;
  copied in-workspace applications can be imported explicitly.
- Implemented breaking cutover: obsolete project and Application resource
  versions are rejected, with no compatibility DTOs or migration branches.
- Remaining: complete IDE dirty-resource UX.

### Phase 2: Release Publisher

- Implemented: add `ProjectReleasePublisher`, scoped cross-context source
  validation, immutable application-source artifacts, verified content hashes,
  and Project Snapshot release-only runtime loading.
- Implemented: route release-identified device commands through release
  capability metadata and frozen Engineering device bindings.
- Implemented: release manifest schema v3 freezes the exact Application project
  path plus canonical Flow IR v1; dynamic Python/Blockly children execute as
  aggregate Runtime steps/commands with
  action/parent/sequence identity and terminal propagation.
- Implemented: direct simulated/process-definition starts are gated to an
  explicitly enabled Development/Test host.
- Implemented foundation: declarative Runtime Action Contract v1 for five
  built-ins with canonical JSON/hash and strict validation.
- Next: server-side Blockly workspace compilation, exact block/contract and
  provider/plugin locks, package signing, and an active-release pointer.

### Phase 3: One-Shot Runner

- Implemented: `OpenLineOps.Runner run <project>` selects and synchronously
  executes an immutable Project Snapshot with JSON output and stable exit codes.
- Remaining: package/verify/deploy commands, `.olopkg`, durable state, station
  lease, production Python/plugin trust policy, and recovery.

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

Criteria 1, 2, and 4 are substantially in place. The local Project Snapshot
portion of criterion 5 is implemented for Studio and the one-shot Runner, and
release/hash failures return deterministic errors. The remaining criteria
depend on richer Studio editor behavior, static Blockly contract compilation,
provider/plugin locks, `.olopkg` packaging/signing/deployment, service/queue and
recovery state, and production preflight. Semantic 3D layout is intentionally
later than these foundations.
