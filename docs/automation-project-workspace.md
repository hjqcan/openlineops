# Automation Project Workspace

Last updated: 2026-07-11

## Purpose

OpenLineOps Studio uses the same project-first interaction model as a mature
IDE: create or open a Project, select one portable Application, edit its
resources, publish an immutable snapshot, and run or monitor the published
result. A finished Project can also run through the headless Runner without
opening Studio.

OpenLineOps differs from property/script-first sequencers by making the
semantic line layout and Blockly flow the primary authoring surfaces. Explicit
PythonScript actions provide controlled advanced behavior.

## Project and Application boundary

An Automation Project is represented by one `<project-id>.oloproj` file. It
contains project identity, Application references, publication history, and the
active snapshot reference.

An Application is represented by one `.oloapp` file inside its own directory.
That directory contains every editable resource owned by the Application and
does not store its host Project id. It can be copied into another Project and
imported without rewriting its internal files.

Two Applications in the same Project are isolated. They may use identical
local resource ids and filenames because API and repository operations always
carry an explicit Project/Application scope.

## Directory structure

```text
project-root/
  <project-id>.oloproj
  applications/
    <application-directory>/
      <application-id>.oloapp
      topology/
        topology-<safe-id>--<hash>.json
      layouts/
        layout-<safe-id>--<hash>.json
      production/
        lines/
          <line-definition-id>/
            line.json
            programs/
              ... application-owned vendor executables/assets ...
      flows/
        process-<safe-id>--<hash>/
          flow.json
          nodes/
            node-<safe-id>--<hash>/
              workspace.<sha256>.blockly.json
              source.<sha256>.py
      blocks/
        custom/
          block-<safe-id>--<hash>/
            versions/
              version-000001.json
      configuration/
        ... current Application configuration resources ...
  releases/
    ... immutable content-addressed Project releases ...
```

Only a Blockly node owns a `workspace.*.blockly.json` artifact. Only an explicit
PythonScript node owns a `source.*.py` artifact. Blockly does not persist a
generated Python mirror.

## Studio workflow

### Start Center

When no Project is open, Studio shows a start page with recent Projects,
search, Create Project, Open Project, and Open Folder actions. It does not show
an empty runtime dashboard.

### Project Explorer

After opening a Project, the Explorer shows its Applications. Selecting an
Application scopes all workbenches:

- 2D Layout;
- Production Lines;
- Flows & Scripts;
- Configuration;
- Devices & Drivers;
- Run & Monitor;
- Trace Evidence;
- Extensions.

Changing Application cannot silently reuse the prior Application's selected
resource or runtime snapshot.

### Edit mode

Edit mode operates on Application files. The 2D Layout workbench creates and
arranges Station Systems, child Systems, Groups, and Slots. The Flow Designer
authors Blockly, PythonScript, commands, decisions, and transitions. The Line
Designer binds `OperationDefinition` nodes to Station Systems and published
flows, then connects them with sequence, judgement, typed-condition, bounded
rework, parallel-fork, and parallel-join routes.

### Publish

Publishing validates every cross-reference and writes an immutable release.
The Project manifest records the release digest and selected Application. A
published definition is read-only; further edits create a new draft/version.

### Run mode

Run mode is read-only. Studio or Runner opens the release, verifies content
hashes, maps canonical Flow IR to executable Runtime actions, and projects
Station/target state, active Production Units, queues, material movement, and
Slot occupancy onto the published layout. A submitted `ProductionRun` is
asynchronous; the Coordinator can advance different units on different Stations
at the same time.

Returning to Edit mode never mutates or resumes a published snapshot.

## Application topology

The topology schema contains only:

- `systems`;
- `capabilities`;
- `driverBindings`;
- `slotGroups`;
- `slots`.

`AutomationSystem` is the one canonical System identity. `StationSystem`
inherits it and uses the same `systemId` in Production, Flow, Runtime, and
Traceability. A Group belongs to a Station. A Slot belongs to a Group and that
same Station.

Removed node/module target kinds and fields are not accepted.

## Shared 2D and 3D layout

The layout schema is hierarchical. Each element has a semantic target and a
`parentElementId`; child coordinates are local to their parent. The editor
supports drag, keyboard nudge, resize/geometry editing, and nested selection.
Moving a Station carries its visual subtree automatically.

Edit mode renders design semantics. Monitor mode renders the same layout with
live state keyed by `systemId` and target identity. It never stores status in
the layout JSON.

The semantic 3D renderer projects the same published topology and state. In
edit mode, dragging a 3D block updates the same parent-local geometry used by
2D; in monitor mode, both dimensions display the same live target state. Studio
does not keep a second topology file or a disconnected 3D model.

## Flow resources

`flow.json` stores the process graph and references content-addressed node
artifacts. Blockly workspaces are compiled on the server to current Flow IR.
Every action has a stable action id, source mapping, target, capability,
command, canonical input, timeout, and retry policy.

Valid target kinds are `System`, `Capability`, `Driver`, `SlotGroup`, `Slot`,
and `ProductionUnit`.

PythonScript is explicit and bounded. It cannot emit an `automation_plan` or
create runtime actions that were absent from the published Flow IR.

## Production resources

`production/lines/<id>/line.json` is an independent Application resource. It
contains one `ProductModelDefinition`, an entry Operation, Station-System-bound
`OperationDefinition` nodes, `RouteTransition` edges, published Flow references,
one `routeLayout` with an exact bounded integer position for every Operation,
and optional external-program adapters. There is no duplicate Station
definition: `stationSystemId` points directly at the canonical `StationSystem`.

The route semantics and its layout use one atomic file replacement and one
revision/ETag. Drag, Auto Arrange, Save All, external-change conflict handling,
and Application copying therefore cannot produce a detached or stale layout.
The strict reader rejects a missing layout, missing or extra Operation
position, case ambiguity, duplicates, and coordinates outside `0..100000`.

An external executable path is Application-relative under the line resource, or
the adapter names an exact provider key. Production never starts it directly;
the referencing Flow action uses the frozen provider route and the same command,
timeout, cancellation, judgement, incident, and artifact lifecycle as every
other action.

The editable definition describes the process, not work in progress. Runtime
owns `ProductionUnit`, `ProductionLot`, `Carrier`, `MaterialGenealogyLink`,
`MaterialLocation`, and `SlotOccupancy`. Slot state is exactly `Available`,
`Reserved`, `Occupied`, `Running`, `Blocked`, or `Offline`, and every reservation
or occupancy binds a Production Unit or Carrier.

## Persistence rules

- All paths stored in editable documents are normalized Application-relative
  paths.
- Absolute paths, `..` traversal, reparse-point escape, ambiguous casing, and
  writes outside the selected Application are rejected.
- Resource ids and document paths are case-sensitive domain values even on a
  case-insensitive filesystem; ambiguous case is an error.
- Writes are atomic and idempotent.
- JSON readers reject unknown fields, missing required fields, non-current
  schema versions, and noncanonical persisted artifacts where canonical form is
  required.
- There are no legacy readers, background migrations, database backfills, or
  fallback global repositories.

## Publication boundary

A release freezes and hashes:

- `.oloapp` and all referenced resource documents;
- the strict current Topology and hierarchical Layout documents;
- Production line definitions and required Application programs;
- process graph, Blockly workspace, Python source, Flow IR, and source maps;
- custom block version and runtime action contract hash;
- configuration for referenced Station Systems;
- capability and Driver binding resolution;
- complete provider package tree, manifest, entry assembly, command/file
  inventory, runtime identifier, contract, and ABI.

Runtime accepts complete Project/Application/snapshot identity only. A missing
release resource or provider is a hard failure. Runtime never consults live
plugin inventory, mutable Engineering state, global Process storage, or editable
Application files.

## Headless execution

The Runner opens a `.oloproj`, selects a published snapshot, verifies the
release, submits one asynchronous Production Run, waits for its terminal state,
emits the two status axes plus operation/route/trace details, and exits with an
outcome code. It uses the same release loader, launcher, Coordinator, and Runtime
lifecycle as Studio. It is not a second execution implementation.

An unattended Windows Agent executes Station jobs from signed `.olopkg` content.
It verifies signatures and every hash before installing into a read-only
content-addressed cache, deduplicates commands by idempotency key in a local
SQLite inbox, checkpoints execution, and durably buffers results in an outbox.
Normal work uses the job transport; Emergency Stop and Safe Stop use the
independent safety receiver.

## Required verification

Every change to the Project/Application model must include:

- domain and repository tests for strict schemas and isolation;
- cold-restart and folder-move tests;
- release tamper/fail-closed tests;
- API tests scoped to Project and Application;
- desktop typecheck/build;
- a real Electron E2E that creates/opens a Project, edits layout and Flow,
  publishes, runs the Project Snapshot, and verifies monitoring state;
- screenshot inspection at the supported minimum desktop size.
