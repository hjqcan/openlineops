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
release. Provider routes and plugin packages are resolved and locked by exact
identity and content before publication; Runtime does not consult live authoring
inventories.

IDE runs and standalone Runner execution both use the same immutable release
boundary. Editable Application source is never an execution fallback.

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

Edit mode exposes the Application's hierarchical 2D System layout, production
line composition, configuration, device binding, Blockly flow, Python, block
catalog, and trace-navigation editors.

Saving persists project source. Publishing performs cross-context validation
and produces a release. Save and Publish are separate operations.

### Run Mode

Run mode opens the same published 2D layout as a read-only operational
workspace, with Station and target state overlaid by stable System identity. It
is not another table-first dashboard page.
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
- Blockly and Python are distinct flow node kinds. Blockly is declarative and
  primary; Python is an explicit advanced dynamic-code node.
- Inspector displays the selected Node, Module, Driver Binding, Group, Slot,
  Flow Node, Block, or Layout Element.
- Compiled Flow IR, workspace JSON, validation errors, Python source for explicit
  PythonScript nodes, runtime output, and trace details belong in the Bottom Panel.
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
      production/
        lines/
          <line-id>/line.json
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

The current release manifest records the exact Application project path plus
Topology v1, hierarchical Layout v1, production definitions, process and
version, Station-System configuration, capability bindings, target references,
exact Blockly block versions and contract hashes, canonical Flow IR JSON and
SHA-256, complete content-addressed provider package locks, every copied file's
size and SHA-256, and a release content digest. Package signatures and an
active-release pointer remain future deployment outputs, never trusted editable
source inputs.

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

This is a local immutable release boundary. It freezes complete provider package
trees and hashes but does not yet create or sign an `.olopkg`, update an
active-release pointer, or provide deployment-package verification. The one-shot
Runner consumes this local release boundary; it is not an `.olopkg` verifier.

Project Snapshot launch and the Runner are the only execution routes. Simulated,
direct process-definition, and global-repository runtime-start endpoints have
been deleted rather than gated behind an environment flag.

## Frozen Flow IR And Action Lifecycle

Flow IR freezes process/node/transition identity, loop metadata, Python source
hashes, and statically compiled Blockly actions. A Blockly workspace is parsed
server-side; every block resolves to an exact definition version and canonical
Runtime Action Contract hash. Each emitted action carries its source block id,
action type, explicit topology target, capability, command, payload, timeout,
retry policy and trace identity.

Blockly never generates Python. PythonScript remains one explicit published
action and uses the governed script executor; it cannot emit undeclared child
actions. Every Flow action is represented by an aggregate Runtime step and
command with required action and target identity, so failure, rejection,
cancellation, timeout, monitoring, and trace use the same lifecycle.

## Declarative Runtime Action Contracts And Dependency Locks

Processes.Application defines strict
`openlineops.runtime-action-contract/v1` contracts with deterministic canonical
serialization and SHA-256. Built-in, Application-local custom, and compatible
plugin-generated blocks use only declarative `deviceCommand`, `delay`, and
`resultPatch` emits. Legacy Python templates and arbitrary frontend generators
are rejected.

Publication validates block fields and topology targets, freezes exact block
version plus contract hash, resolves Flow IR commands to provider packages, and
copies exact package trees into content-addressed release storage. Dependency
locks record provider binding, package/plugin id, version, full-tree hash,
manifest and entry hashes, contract, RID, ABI, commands, and file inventory.
Runtime verifies the release and loads only those frozen packages; it has no
live plugin-inventory fallback.

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
- Systems, Groups, Slots, and hierarchical layout needed by Runner/operator UI;
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
Studio Run command ----+
HTTP API controller ---+--> IProjectReleaseProductionRunLauncher --> IProductionRunRunner
CLI Runner ------------+                                      |--> ordered Runtime Sessions
```

The caller supplies a Production Run id. The Run and every Stage-to-Session link
are persisted before that Stage can touch hardware. Reusing a Run id is rejected,
and interrupted Runs terminate without replaying device commands.

## Runner And CLI

`src/OpenLineOps.Runner` is an implemented one-shot headless executable. It
opens a project directory or `<projectId>.oloproj`, selects an explicit or active
Project Snapshot, requires an immutable release descriptor, runs it through
`ProjectReleaseProductionRunLauncher`, writes one JSON result using the Runner
output schema to standard output, and exits after the Production Run reaches a
terminal state. All non-`.oloproj` project formats are rejected.

Current command:

```powershell
dotnet run --project src/OpenLineOps.Runner/OpenLineOps.Runner.csproj -- `
  run C:\Projects\LineA --snapshot active `
  --dut DUT-001 --batch BATCH-001 --fixture fixture-a `
  --device device-a --actor operator-a
```

`--dut` and `--actor` are required. `--snapshot` defaults to `active`, and
`--run-id` defaults to a new GUID; supplying it makes retries explicitly
idempotent. Batch, fixture, and device are optional trace inputs. Runner rejects
draft-only projects and snapshots without a release. Runtime configuration comes from `appsettings.json`,
`appsettings.<environment>.json`, and environment variables.

Runner stores Runtime and Traceability state in a path-scoped directory under
the current user's local application data. An exclusive per-project lease
prevents two one-shot Runner processes from touching the same line concurrently;
startup recovery terminalizes interrupted Run and Session records without
replaying a command.

Implemented stable exit codes:

- `0`: completed successfully, or help displayed;
- `2`: command-line usage error;
- `3`: project path or manifest could not be opened;
- `4`: requested or active snapshot could not be selected;
- `5`: selected snapshot has no immutable release;
- `6`: immutable release verification or Production Run start was rejected;
- `7`: Production Run failed;
- `8`: execution was canceled;
- `70`: unexpected configuration/internal error.

This Runner is synchronous and one-shot. It is not a daemon, Windows Service,
queue, remote Agent, operator UI, or station-lease manager. It does not package,
sign, verify, cache, or deploy `.olopkg` files. A Run interrupted by process
termination is recovered to an honest terminal failure and never replays
hardware commands.

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
- Implemented: independent hierarchical 2D layout editor with Station/System,
  Group, and Slot drag composition; Run enters the same layout in Monitor mode.
- Remaining: richer editor tabs, dirty-resource workflow, and complete
  Pause/Continue/Stop operator state behavior.

### Phase 1: Durable Project Source

- Implemented: persist Projects, Topology, Layout, Production Lines, Processes,
  Blockly/Python, Blocks, and Engineering Configuration by explicit
  project/application scope.
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
- Implemented: the current release manifest freezes the exact Application
  project path, canonical Flow IR, static Blockly actions, exact block contract
  locks, and content-addressed provider package dependency locks.
- Implemented breaking cutover: simulated/direct/global Process starts and
  repositories were deleted; Project Snapshot is the only runtime start path.
- Implemented: declarative Runtime Action Contract v1 for built-in, custom and
  plugin-generated blocks, with server-side workspace compilation, canonical
  JSON/hash and strict validation.
- Next: package signing and an active-release pointer.

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
