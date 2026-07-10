# Automation Project Workspace

Last updated: 2026-07-10

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
authors Blockly, PythonScript, commands, decisions, and transitions. Production
Lines bind ordered stages to Station Systems and published flows.

### Publish

Publishing validates every cross-reference and writes an immutable release.
The Project manifest records the release digest and selected Application. A
published definition is read-only; further edits create a new draft/version.

### Run mode

Run mode is read-only. Studio or Runner opens the release, verifies content
hashes, maps canonical Flow IR to executable Runtime actions, and projects
Station/target state onto the published layout.

Returning to Edit mode never mutates or resumes a published snapshot.

## Application topology

Topology schema v2 contains only:

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

## 2D layout and future 3D

Layout schema v2 is hierarchical. Each element has a semantic target and a
`parentElementId`; child coordinates are local to their parent. The editor
supports drag, keyboard nudge, resize/geometry editing, and nested selection.
Moving a Station carries its visual subtree automatically.

Edit mode renders design semantics. Monitor mode renders the same layout with
live state keyed by `systemId` and target identity. It never stores status in
the layout JSON.

3D will be a renderer over the same published topology and state. Studio must
not offer a second topology file or fake a 3D model disconnected from 2D.

## Flow resources

`flow.json` stores the process graph and references content-addressed node
artifacts. Blockly workspaces are compiled on the server to current Flow IR.
Every action has a stable action id, source mapping, target, capability,
command, canonical input, timeout, and retry policy.

Valid target kinds are `System`, `Capability`, `Driver`, `SlotGroup`, `Slot`,
and `Dut`.

PythonScript is explicit and bounded. It cannot emit an `automation_plan` or
create runtime actions that were absent from the published Flow IR.

## Production resources

`production/lines/<id>/line.json` is an independent Application resource. It
contains a DUT model, Workstations identified by `stationSystemId`, ordered
stages, published Flow references, and optional external-test adapters.

An external executable path is Application-relative under the line resource,
or the adapter names an exact provider key. Production never starts it directly;
the stage compiles to one standard runtime action and uses the frozen provider
route.

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
- the current Topology v1 and hierarchical Layout v1;
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
release, executes it, emits status/trace output, and exits with an outcome code.
It uses the same release loader and Runtime lifecycle as Studio. It is not a
second execution implementation.

Future station services, deployment agents, leases, recovery, and signed
packages must preserve this boundary: immutable release in, monitored runtime
events out.

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
