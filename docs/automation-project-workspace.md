# Automation Project Workspace Architecture

Last updated: 2026-07-10

## Purpose

OpenLineOps should behave like a project-oriented automation workbench. A user
creates or opens an automation project, composes the production-line model,
edits the site layout, authors execution flows visually, runs those flows, and
traces the resulting data as one cohesive engineering asset.

The runtime platform foundations are already useful, but the product center is
the automation project. Backend contexts, desktop navigation, storage, and
publishing should all make that project lifecycle obvious.

The detailed composable building block model is defined in
`docs/composable-automation-model.md`, with the implementation architecture in
`docs/composable-building-block-architecture.md`. This document focuses on the
workspace workflow around that model.

The IDE shell, Edit/Run mode boundary, immutable project release, implemented
one-shot Runner, and future deployable package, service, queue, recovery, and
Agent host model are defined in `docs/automation-ide-product-shell.md`.

## Product Workflow

The target workflow is:

1. Create or open an automation project from the desktop shell.
2. Define the project's applications, topology, equipment nodes, modules,
   capabilities, driver bindings, slot groups, and slots.
3. Place those topology targets on a two-dimensional top-down site layout.
4. Bind capability requirements to device instances, simulator routes, external
   systems, and plugin commands.
5. Edit execution flows with Blockly as the default authoring surface.
6. Use generated or manually edited Python source through the PythonScript
   integration for flexible hardware control.
7. Publish an immutable project snapshot for runtime execution.
8. Run, monitor, and trace sessions against that published snapshot.
9. Export or share the project package and its trace evidence.

Steps 1 through 8 now have an end-to-end local path: Studio provides the Start
Center and project workbench, Publisher creates an immutable local release, and
Studio or the one-shot headless Runner can execute its Project Snapshot. A
signed deployable package and remote sharing/deployment workflow in step 9 are
not implemented.

This workflow is intentionally different from property-and-script-first test
sequencers. OpenLineOps should be visual-first: the site model and Blockly flow
are the primary authoring experience, while Python remains the controlled escape
hatch for advanced behavior.

## Ubiquitous Language

### AutomationProject

The root user-facing engineering asset. It owns project identity, display name,
description, local project path or package identity, project settings, relative
references to its applications, draft status, and publication history. Its
editable root file is `<projectId>.oloproj`.

### ProjectApplication

An independently movable automation scenario composed by a project. Its
application directory contains one `.oloapp` file and everything it owns:
systems, topology, layouts, processes, Blockly/Python source, configuration,
bindings, scripts, custom blocks, and run targets. The directory does not
persist its host `ProjectId`, so it can be copied into another automation
project and imported without rewriting its internal resources.

### AutomationTopology

The structural model of the application. It owns equipment hierarchy,
equipment nodes, module instances, ports, connections, slot definitions, slot
groups, capability requirements, and driver bindings.

### EquipmentNode

An addressable node in the automation topology. It can represent a site, area,
line, cell, station, unit, fixture, transport, buffer, device mount, external
system, or logical subsystem.

### AutomationModule

A reusable behavior-bearing component attached to an equipment node, such as
vision inspection, robot handling, axis motion, IO, lighting, laser measurement,
barcode scan, fixture clamp, MES adapter, or test instrument control.

### CapabilityContract

A versioned operation contract that a process or block can request. Examples
include axis movement, motor rotation, light output, vision capture, barcode
scan, fixture clamp, and MES upload.

### DriverPackage And DriverBinding

A driver package provides capability implementations through plugin, simulator,
external service, or built-in adapter paths. A driver binding resolves a
capability requirement to a concrete provider inside a project.

### SlotDefinition And SlotGroup

A slot is a stable material endpoint for a DUT, carrier, nest, fixture position,
tray position, or logical work item. A slot group is a controlled material group
such as a fixture nest, tester bank, tray row, buffer lane, or robot pick group.

### SiteLayout

The visual top-down model of the automation site. The first implementation is
two-dimensional, with explicit room for future 3D metadata. It contains layout
elements, coordinates, layers, geometry, labels, visual state, and references
back to slots, groups, systems, or devices.

### ProcessDefinition

The executable flow graph owned by the Processes bounded context. Process nodes
can include Blockly-authored PythonScript nodes, command nodes, decisions,
delays, and future human or measurement nodes.

### PublishedProjectSnapshot

The project manifest's immutable runtime handoff record. It identifies the
application, topology, layouts, published process and version, published
Engineering configuration snapshot, resolved capability bindings, runtime
targets, Blockly block versions, release manifest path, and release content
digest. The referenced release artifact contains the frozen application source,
including the Blockly and Python artifacts used by the process.

## Bounded Context Ownership

### Projects

The Projects bounded context should become the primary product shell. It owns
automation project lifecycle, project manifests, project-local settings,
relative application references, application selection, package metadata, and
publication history. The root project composes application projects; it does
not own their editable resource graphs.

It should not execute hardware commands or validate Python syntax directly.
Those responsibilities stay in Runtime, Devices, Processes, and Infrastructure
adapters.

### Topology

Topology owns the composable model: equipment nodes, module instances, ports,
connections, slots, slot groups, capability requirements, driver bindings, and
site layout drafts.

It should not execute runtime commands. It validates structure and creates the
draft model consumed by project publication.

### Engineering

Engineering continues to own production configuration concepts such as
workspaces, engineering projects, recipes, station profiles, device bindings,
and immutable configuration snapshots.

As Projects becomes first-class, Engineering should become the production
configuration provider consumed by project publication rather than the visible
desktop product shell.

### Processes

Processes owns process definitions, graph validation, process publication,
PythonScript node metadata, Blockly block catalogs, generated block sources,
and custom block version history.

Processes should target capabilities, slots, groups, or equipment nodes through
stable ids and contracts. It should not own topology, layout, or driver binding
state.

### Devices

Devices owns device definitions, device instances, capabilities, command
definitions, command routing, connection state, and plugin-backed execution.

Capability requirements and driver bindings should resolve to Devices concepts
through application ports and immutable published snapshots.

### Runtime

The Project Snapshot runtime path executes the published process and
configuration loaded from the immutable release. It does not re-read the
editable application source or fall back to mutable global configuration
store.

Publisher compiles the published process graph into canonical
`openlineops.flow-ir/v1` JSON and freezes its schema, SHA-256, and content in
release manifest schema v3. Project Snapshot launch validates that frozen Flow
IR and maps it to the executable runtime process instead of reconstructing the
runtime graph from client input.

Python and current Blockly-authored Python nodes retain a Flow IR dynamic-action
slot. When their `automation_plan` result is expanded, each child is created in
the `RuntimeSession` aggregate as its own Runtime step and command. The child
step records an explicit `ActionId`, parent step id, and dynamic sequence; its
command shares the `ActionId`, and the execution context propagates the complete
identity. Child failure, rejection, cancellation, or timeout is persisted and
propagated to the container and session. This closes the former nested-command
trace gap, but it is still runtime expansion with container-level source
mapping, not the future server-side Blockly workspace compiler with
block-id/type/version source maps.

### Traceability

Traceability records runtime sessions, process versions, project snapshots,
layout references, slot/group references, device command records, artifacts,
measurements, and audit entries.

## Editable Project And Application Format

The editable source uses two explicit project boundaries:

```text
project-root/
  <project-id>.oloproj
  applications/
    <application-directory>/
      <application-name>.oloapp
      topology/
      layouts/
      flows/
      configuration/
      bindings/
      blocks/
      scripts/
  releases/
    ...immutable project snapshots...
```

The `.oloproj` file is the automation project entrypoint. It owns project
metadata, project-relative `.oloapp` references, publication snapshots, and the
selected snapshot. Each `.oloapp` owns application identity, display metadata,
and relative links to its application-local resources. Root and child files
are separate aggregates, not fragments of one serialized in-memory object
graph.

Every persisted file under an application root retains `ApplicationId` and its
own resource identity but must not retain the host `ProjectId`. This includes
the `.oloapp`, topology, layout, process, Blockly/Python, Engineering
configuration, binding, script, and custom-block documents. Cross-application
integration must use explicit contracts or references rather than hidden paths
into a sibling directory.

To reuse an application, copy its complete directory from Project A into
Project B's `applications` directory, then import its `.oloapp`. Import validates
identity, relative paths, containment, duplicate ids, and path collisions before
adding a relative reference to Project B's `.oloproj`; it does not recursively
rewrite the copied resources. Requiring the directory to be inside the target
workspace prevents a project from silently linking arbitrary mutable files
elsewhere on disk.

The current `.oloproj` represents one automation project. A future multi-project
solution can add a separate solution artifact; calling this file `.olosln` now
would incorrectly imply that extra aggregation level. Obsolete project
filenames and schema versions are rejected; the format has no compatibility or
automatic migration branch.

SmartMatriX `.ak` files are prior-art reference only, not accepted OpenLineOps
project input. OpenLineOps does not adopt their giant CLR object graph,
absolute-path, or runtime-state serialization model.

## Draft And Publish Model

Project editing is draft-oriented. Users can change layout, group membership,
slot binding, process references, custom Blockly blocks, and Python source while
the project is open. A saved process Draft remains editable and is replaced in
place while preserving its identity and creation time. Publishing makes that
definition immutable; further work starts from a new Draft/version.

Runtime execution should not depend on mutable draft state. Starting a runtime
session requires a published snapshot that freezes:

- Project identity and version.
- Application, topology, equipment node, module, slot group, and slot model.
- Site layout element references.
- Engineering configuration snapshot id.
- Process definition versions.
- Canonical Flow IR schema, JSON, and SHA-256.
- Block catalog versions used by Blockly nodes.
- Generated or manually edited Python source and source hashes.
- Capability contracts and driver bindings resolved for runtime.

The implemented publish API accepts only publication intent: snapshot,
application, process definition, and configuration snapshot ids. The server
resolves topology, all layouts, the published process version, the unique
published Engineering configuration snapshot, topology targets and bindings,
and the current catalog version selected for every block type found in the
Blockly workspace. It rejects a release when required process capabilities are
not declared and bound in both Topology and the selected Engineering
configuration.

The release store copies the complete application source into a staging
directory, records every file's size and SHA-256 digest plus canonical Flow IR
metadata and the exact `.oloapp` path in schema-v3 `release.json`, computes a
digest over the normalized manifest content, and verifies the staged artifact
before an atomic directory move. An existing release id is never overwritten.
The publisher then resolves
the copied source again and requires its semantic metadata and Flow IR to match
the source resolved before the copy. Only after those checks does the project
manifest record the project-relative release manifest path and content digest.

The current on-disk release shape is:

```text
project-root/
  releases/
    release-<safe-snapshot-id>/
      release.json
      source/
        applications/
          <application-folder>/
            <name>.oloapp
            ...frozen application source...
```

Opening a release verifies its schema and identity, expected content digest,
exact file set, per-file sizes and hashes, and safe paths before exposing its
source root. The Project Snapshot runtime launcher verifies that the release
metadata matches the snapshot, verifies the frozen Flow IR schema, canonical
content, hash, and process identity, then loads validation source and
configuration through project-scoped repositories rooted at that frozen source.
Device commands that carry Project Snapshot identity resolve their topology
capability and device binding from the same release; they do not silently fall
back to mutable or global Engineering state.

The development-only `POST /api/runtime/sessions/simulated` and
`POST /api/process-definitions/{id}/runtime-sessions` surfaces are now explicitly
development/test-only. They return `403` unless the host environment is
`Development` or `Test` and
`OpenLineOps:Runtime:DevelopmentStarts:Enabled=true` is also set. Production
execution uses the immutable Project Snapshot route.

## API Shape

The project source API is explicitly scoped by project and application. Studio
uses only the scoped routes:

- `POST /api/automation-projects`
- `GET /api/automation-projects`
- `GET /api/automation-projects/{projectId}`
- `PUT /api/automation-projects/{projectId}/manifest`
- `POST /api/automation-projects/{projectId}/applications/import`
- `PUT /api/automation-projects/{projectId}/applications/{applicationId}`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/topologies`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/topologies`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/topologies/{topologyId}`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/topologies/{topologyId}/nodes`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/topologies/{topologyId}/modules`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/topologies/{topologyId}/slot-groups`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/topologies/{topologyId}/slots`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/topologies/{topologyId}/driver-bindings`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/layouts`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/layouts/{layoutId}`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/processes`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/processes`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/processes/{processDefinitionId}`
- `PUT /api/automation-projects/{projectId}/applications/{applicationId}/processes/{processDefinitionId}`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/processes/{processDefinitionId}/validation`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/processes/{processDefinitionId}/publish`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/process-blocks`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/process-blocks`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/process-blocks/{blockType}/versions`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/engineering/workspaces`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/workspaces`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/workspaces/{workspaceId}`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/engineering/projects`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/projects`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/projects/{engineeringProjectId}`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/engineering/recipes`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/recipes`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/recipes/{recipeId}`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/engineering/recipes/{recipeId}/publish`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/engineering/station-profiles`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/station-profiles`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/station-profiles/{stationProfileId}`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/engineering/projects/{engineeringProjectId}/configuration-snapshots`
- `POST /api/automation-projects/{projectId}/applications/{applicationId}/engineering/projects/{engineeringProjectId}/configuration-snapshots/{snapshotId}/rollback`
- `GET /api/automation-projects/{projectId}/applications/{applicationId}/engineering/projects/{engineeringProjectId}/configuration-snapshots/{fromSnapshotId}/diff/{toSnapshotId}`
- `POST /api/automation-projects/{projectId}/publish`
- `GET /api/automation-projects/{projectId}/snapshots`
- `POST /api/automation-projects/{projectId}/snapshots/{snapshotId}/runtime-sessions`

Desktop file operations such as choosing a `.oloproj`, choosing a project
folder, selecting an in-workspace `.oloapp` for import, or activating a recent
project remain Electron responsibilities. The backend owns project and
application-file validation, path containment, state validation, and
publication behavior. Studio uses the nested project/application routes so
identical resource ids in two applications do not collide.

## Electron Workbench

The desktop shell now uses a project-first experience. Before a project is
open, a VS-inspired but OpenLineOps-specific Start Center presents searchable,
time-grouped recent projects plus Create Project and Open Project Folder. It
does not expose unrelated engineering or runtime dashboards. Opening a project
transitions into the IDE workbench with explicit project and application state:

- Start window with new project, open project, and recent projects.
- Project explorer sidebar for applications, topology, equipment nodes, modules,
  slot groups, slots, processes, devices, traces, and plugins.
- Site layout editor as a first-class workbench.
- Process editor scoped to the active project and selected application,
  equipment node, slot group, or slot.
- Blockly toolbox populated by built-in blocks, custom blocks, and compatible
  plugin command manifests.
- Python preview and manual code mode inside the process editor, not as the
  primary workflow.
- Runtime launch panel that selects a published project snapshot instead of
  asking users to manually provide unrelated ids.

Richer editor-tab/dirty-state behavior, deeper topology editing, and additional
IDE polish remain; the entry-to-workbench lifecycle itself is implemented.

## Site Layout Model

The first site layout model should be simple and stable:

- `layoutId`
- `projectId`
- `coordinateSystem`: default `TopDown2D`
- `canvasWidth`
- `canvasHeight`
- `units`: pixels, millimeters, or meters
- `elements`
- `layers`

Each element should carry:

- stable element id
- element kind, such as slot, group, system, zone, fixture, device, label, path,
  or region
- x/y position
- width/height or path geometry
- rotation
- layer id
- visual label
- reference target, such as slot id, group id, system id, or device instance id
- optional future 3D metadata, such as z, depth, height, mesh reference, or
  transform

The two-dimensional model should not prevent a later 3D renderer. The domain
should store layout semantics and references, while the renderer decides whether
to draw them with Canvas, SVG, WebGL, or Three.js.

## Blockly And PythonScript

Blockly is the primary process node kind. A block represents an
automation intent, such as moving an axis, turning on a light, rotating a motor,
waiting, executing a plugin command, or invoking a custom project operation.

Custom blocks must store:

- Blockly JSON definition.
- block type and version.
- display category.
- canonical Runtime Action Contract and SHA-256.
- declared typed inputs and emitted action.
- explicit target kind/id fields for target-bound actions.

The server compiles the workspace directly to static Flow IR. Hardware control
always passes through the runtime/device/plugin command path so command results,
timeouts, failures, cancellation, monitoring and trace records remain consistent.

An explicit `PythonScript` node remains available for advanced users, but it is
not a mode or generated representation of a Blockly node.

The Processes application layer now also defines strict
`openlineops.runtime-action-contract/v1` typed contracts with deterministic
canonical JSON and SHA-256. Built-in, custom and compatible plugin-generated blocks have declarative contracts
for `deviceCommand`, `delay`, or `resultPatch` emits and safe literal, field,
context, object, and array values. Unknown fields, scripts/templates/raw
expressions, dynamic capability/command selection, duplicate JSON properties,
non-finite numbers, hostile nulls, and non-canonical documents are rejected.

This contract is the Blockly execution authority. Publication resolves every
workspace block id/type/field set to an exact definition version and contract
hash, validates topology targets, emits source-mapped static Flow IR actions,
and freezes content-addressed provider package locks. Runtime rejects missing or
mismatched release artifacts and never falls back to the live catalog.

## One-Shot Headless Runner

`src/OpenLineOps.Runner` can run an existing immutable release without opening
Studio:

```powershell
dotnet run --project src/OpenLineOps.Runner/OpenLineOps.Runner.csproj -- `
  run C:\Projects\LineA --snapshot active `
  --serial SN-001 --batch BATCH-001 --fixture fixture-a `
  --device device-a --actor operator-a
```

The target can be a project directory or its `<projectId>.oloproj` file; every
other project format is rejected. `--snapshot` accepts an id or defaults to
`active`. Runner opens the project, selects the
snapshot, requires its immutable release descriptor, executes it through the
same release-only launcher, writes one JSON result using Runner output schema
v1, and returns a stable exit code: `0` success, `2` usage, `3` project open,
`4` snapshot selection, `5` immutable release required, `6` start rejected,
`7` non-completed runtime terminal state, `8` canceled, or `70`
internal/configuration failure.

The implemented Runner is deliberately one-shot and synchronous. It does not
package or verify `.olopkg` files, deploy releases, host a service/API/operator
UI, queue requests, acquire a station lease, persist durable recovery state,
resume interrupted hardware work, sign packages, or freeze/verify complete
plugin binaries. Those remain separate production-hardening phases.

## Implementation Slices

### Slice 1: Project Domain Skeleton

Add a Projects bounded context with `AutomationProject`, strong typed ids,
project manifest, project lifecycle, and behavior tests.

### Slice 2: Topology And Capability Model

Add equipment nodes, module templates, module instances, ports, connections,
capability contracts, driver bindings, slot definitions, and slot groups. Keep
invariants in the domain, including stable ids, allowed child-node policies,
compatible ports, unique slot addresses, and valid slot/group references.

### Slice 3: Site Layout Draft

Add `SiteLayout` and layout element behavior with validation for element ids,
coordinates, target references, and duplicate placement.

### Slice 4: Persistence And API

Add application services, project/application-scoped repository ports,
project-folder source adapters, API controllers, and host-level API tests for
create/list/open/update/publish. Global databases can be indexes or runtime
state, but are not the authority for editable project source.

### Slice 5: Desktop Project Shell

Add new/open/recent project experience, project explorer, active project state,
and a first site layout editor that can place equipment nodes, modules, slot
groups, slots, devices, labels, and zones on a top-down canvas.

### Slice 6: Runtime Handoff

Publish project snapshots and update runtime launch so the desktop starts
sessions from a published project snapshot instead of manually assembled ids.

## Current State And Remaining Work

The repository now has a project-first Studio shell plus a root `.oloproj` and
independent `.oloapp` application projects. Durable application-local source
covers Topology, SiteLayout, ProcessDefinition, Blockly workspaces, Python,
versioned custom Blockly block definitions, and Engineering configuration.
Engineering workspaces, engineering projects and their configuration snapshots,
recipes, and station profiles are stored under the active application's
`configuration` directory. Every project-source repository receives the same
explicit `(project, application)` scope at runtime, while the files inside the
application directory persist only application/resource identity so the
directory remains host-project-independent. Topology and layout use versioned
JSON; a process uses `flow.json` as an atomic commit pointer to content-addressed
Blockly/Python artifacts with verified SHA-256 digests; custom blocks use
immutable `version-000001.json` source files; Engineering resources use
schema-versioned JSON and atomic replacement. The same local topology, layout,
process, block, and Engineering ids are therefore valid in two applications.

A cold-restart API test destroys the first host, moves the complete project
folder, opens its `.oloproj` in a fresh host, and restores two isolated
applications, including their custom blocks and Engineering configuration.
Portability tests also copy a complete application directory from one project
to another and read its resources without rewriting internal host identity.
Persistence tests reject a modified Python artifact whose digest no longer
matches `flow.json`.

The cross-context immutable publisher and artifact store are now implemented.
A Project Snapshot launch verifies and reads its release rather than editable
source, and release-identified device command routing reads release-frozen
capability metadata and Engineering device bindings. Project files containing
snapshots without release metadata are invalid and rejected on open.

Release manifest schema v3 now freezes the original Application project path
plus canonical Flow IR v1, and dynamic Python/Blockly child actions have
aggregate Runtime action, parent, and sequence
identity with persisted lifecycle propagation. The old direct-start endpoints
are isolated behind the explicit Development/Test-only gate. A one-shot
headless Runner now executes an immutable Project Snapshot and returns stable
JSON/exit codes.

The next Blockly boundary is to consume Runtime Action Contract v1 through a
server-side workspace compiler, pin block version plus contract hash, emit
static child action source maps, and freeze complete plugin/provider dependency
locks. Longer-running execution still needs a host-neutral run service, durable
queue/state, station lease, recovery policy, package signing/verification, and
deployment tooling.

Studio now has a project-first Start Center, explicit Application selection,
and editable 2D layout geometry. It still needs richer topology editing,
editor-tab and dirty-state behavior, and general IDE polish. Semantic 3D layout
and rendering remain a later phase after the 2D, execution, and deployment
boundaries are stable.
