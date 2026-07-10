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

The IDE shell, Edit/Run mode boundary, immutable project release, deployable
package, CLI, Runner, and Agent host model are defined in
`docs/automation-ide-product-shell.md`.

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

This workflow is intentionally different from property-and-script-first test
sequencers. OpenLineOps should be visual-first: the site model and Blockly flow
are the primary authoring experience, while Python remains the controlled escape
hatch for advanced behavior.

## Ubiquitous Language

### AutomationProject

The root user-facing engineering asset. It owns project identity, display name,
description, local project path or package identity, project settings, selected
application model, layout model, draft status, and publication history.

### ProjectApplication

A logical application or automation scenario inside the project. It groups the
systems, layouts, processes, and run targets needed for a specific production
line, station family, or test application.

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

The immutable runtime handoff artifact. It freezes the project composition,
site layout, engineering configuration snapshot, process versions, device
bindings, block catalog versions, generated Python source, and trace identity
metadata used by runtime sessions.

## Bounded Context Ownership

### Projects

The Projects bounded context should become the primary product shell. It owns
automation project lifecycle, project manifests, project-local settings,
application selection, package metadata, and publication history.

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

Runtime executes published process versions against published configuration and
project snapshots. It should consume a resolved execution plan rather than read
draft project state.

Runtime maps Blockly-generated automation plans to normal command execution so
hardware control remains traceable and cancelable.

### Traceability

Traceability records runtime sessions, process versions, project snapshots,
layout references, slot/group references, device command records, artifacts,
measurements, and audit entries.

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
- Block catalog versions used by Blockly nodes.
- Generated or manually edited Python source and source hashes.
- Capability contracts and driver bindings resolved for runtime.

## API Shape

The project source API is explicitly scoped by project and application. The
legacy global topology/layout endpoints remain temporarily for compatibility,
but Studio uses the scoped routes:

- `POST /api/automation-projects`
- `GET /api/automation-projects`
- `GET /api/automation-projects/{projectId}`
- `PUT /api/automation-projects/{projectId}/manifest`
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

Desktop file operations such as choosing a folder or recent project path remain
Electron responsibilities. The backend owns project state validation and
publication behavior. The legacy global Engineering and process-block routes
remain compatibility surfaces; Studio uses the nested project/application
routes so identical resource ids in two applications do not collide.

## Electron Workbench

The desktop shell should move toward a project-first experience:

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

Blockly remains the default process authoring mode. A block represents an
automation intent, such as moving an axis, turning on a light, rotating a motor,
waiting, executing a plugin command, or invoking a custom project operation.

Custom blocks must store:

- Blockly JSON definition.
- Python code template.
- block type and version.
- display category.
- declared inputs and outputs when available.
- optional target capability or project slot constraints.

The generated Python should emit an automation plan that Runtime dispatches
through command execution. Direct hardware control should still pass through
the runtime/device/plugin command path whenever possible so command results,
timeouts, failures, and trace records remain consistent.

Manual Python editing remains available for advanced users, but it is not the
primary product model.

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

## Current Gap

The repository now has a project-first Studio shell plus durable project source
for Topology, SiteLayout, ProcessDefinition, Blockly workspaces, Python,
versioned custom Blockly block definitions, and Engineering configuration.
Engineering workspaces, engineering projects and their configuration snapshots,
recipes, and station profiles are stored under the active application's
`configuration` directory. Every project-source repository is keyed by the
same explicit `(project, application)` scope. Topology and layout use versioned
JSON; a process uses `flow.json` as an atomic commit pointer to content-addressed
Blockly/Python artifacts with verified SHA-256 digests; custom blocks use
immutable `version-000001.json` source files; Engineering resources use
schema-versioned JSON and atomic replacement. The same local topology, layout,
process, block, and Engineering ids are therefore valid in two applications.

A cold-restart API test destroys the first host, moves the complete project
folder, opens its manifest in a fresh host, and restores two isolated
applications, including their custom blocks and Engineering configuration.
Scoped runtime launch resolves the requested configuration snapshot from the
application-local Engineering source before using the legacy global
compatibility store. Persistence tests also reject a modified Python artifact
whose digest no longer matches `flow.json`.

The remaining release gap is the cross-context immutable publisher: it must
compile versioned Flow IR and freeze resolved capability, device, provider,
plugin, station, and secret-reference bindings instead of trusting client input.
The separate headless Runner, Agent, and CLI hosts must then verify and execute
that same release. The desktop now has explicit Application selection and
editable 2D layout geometry; it still needs full topology CRUD and richer
editor-tab/dirty-state handling.
