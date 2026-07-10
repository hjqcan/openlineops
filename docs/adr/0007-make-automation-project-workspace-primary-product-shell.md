# ADR-0007: Make Automation Project Workspace The Primary Product Shell

## Status

Superseded by [ADR-0009](0009-model-production-lines-and-compile-blockly-directly.md)

## Date

2026-07-09

## Context

OpenLineOps already has bounded contexts for engineering configuration, process
orchestration, runtime execution, devices, plugins, traceability, and
operations. It also has an Electron desktop shell and a Blockly-first
PythonScript flow editor.

Those foundations are necessary, but they do not by themselves express the
target user experience. The intended product is a project-oriented automation
workbench where users create or open automation projects, compose line assets,
edit a visual site layout, author flows with Blockly, run those flows through
PythonScript and device/plugin commands, and trace the results.

## Decision Drivers

- The desktop product should make automation projects the primary user mental
  model.
- Project state must connect layout, devices, flows, scripts, runtime, and
  traceability.
- Draft editing must be separated from immutable runtime execution snapshots.
- Blockly-first authoring must remain the primary flow experience.
- The architecture must leave room for a future 3D site layout without forcing
  3D into the first implementation.

## Considered Options

### Option 1: Keep EngineeringProject as the visible product shell

- Pros: Uses the current Engineering bounded context.
- Cons: EngineeringProject is too narrow for the full user workflow. It does
  not naturally own topology, project-local explorer state, equipment nodes,
  modules, capability contracts, slot groups, slots, recent projects, or
  desktop project packaging.

### Option 2: Make ProcessDefinition the visible product shell

- Pros: Blockly and PythonScript work is already centered on processes.
- Cons: A process definition cannot own the physical layout, application model,
  device/slot topology, project package, or multiple flows inside one project.

### Option 3: Add AutomationProject as the primary shell and bounded context

- Pros: Matches the user workflow, gives the desktop a clear project model,
  separates draft project editing from runtime snapshots, and allows existing
  contexts to keep their responsibilities.
- Cons: Adds another bounded context and requires explicit mapping between
  project composition, engineering snapshots, process versions, and runtime
  launch.

## Decision

OpenLineOps will make Automation Project Workspace the primary product shell.

A new Projects bounded context should own automation project lifecycle,
project manifests, project-local settings, application selection, package
metadata, recent project metadata, and project-level publication snapshots.

A companion Topology model should own equipment nodes, automation modules,
ports, connections, capability requirements, driver bindings, slot definitions,
slot groups, and site layout drafts.

Existing contexts remain responsible for their current domains:

- Engineering owns production configuration and immutable configuration
  snapshots.
- Processes owns flow definitions, Blockly/PythonScript metadata, block
  catalogs, and process publication.
- Devices owns device definitions, instances, capabilities, command routing,
  and connection state.
- Runtime owns execution sessions and command dispatch.
- Traceability owns runtime evidence and audit records.

## Rationale

The target workflow is closer to a desktop project workbench than to a single
sequence editor. A user should not have to manually stitch together unrelated
ids for workspace, station, process, device, and trace data. The project should
be the cohesive artifact that binds those assets together.

Keeping Projects and Topology separate from Processes and Runtime preserves DDD
boundaries. It allows Blockly and PythonScript to stay in the flow authoring
path while the project model owns package lifecycle and the topology model owns
layout and composition.

## Consequences

### Positive

- The product has a clear user-facing root concept.
- Electron can move to new/open/recent project workflows.
- Topology, site layout, and future 3D modeling have an owning model.
- Runtime can launch from published project snapshots.
- Trace records can include project, topology, layout, equipment node, module,
  slot group, and slot references.

### Negative

- A new bounded context adds projects, DTO mapping, persistence, and tests.
- Project publication must coordinate versions from Engineering, Processes,
  Devices, and block catalogs.
- There is risk of duplicating Engineering concepts unless context ownership is
  kept explicit.

### Risks And Mitigations

- Risk: Projects becomes a large context that absorbs all other domains.
  Mitigation: Projects owns lifecycle and publication. Topology owns
  composition. Execution, devices, processes, and traceability stay in their own
  contexts.

- Risk: Layout rendering details leak into the domain model.
  Mitigation: Store layout semantics, geometry, and references in the domain.
  Keep Canvas, SVG, WebGL, and Three.js decisions in Electron adapters.

- Risk: Manual Python editing becomes the dominant workflow.
  Mitigation: Keep Blockly as the default editor and treat Python as preview,
  generated artifact, and advanced override.

## Implementation Notes

- Add `docs/automation-project-workspace.md` as the target workspace guide.
- Add `docs/composable-automation-model.md` as the detailed building block
  model.
- Add a planned milestone for Automation Project Workspace and Site Layout.
- First implementation should be two-dimensional but include future 3D metadata
  extension points.
- Project snapshots must freeze process versions, block versions, generated
  Python source hashes, layout references, and resolved runtime bindings.

## Related Decisions

- ADR-0009: Model Production Lines Separately And Compile Blockly Directly (supersedes this decision).
- ADR-0001: Use Modular Monolith First.
- ADR-0002: Enforce DDD Layering And Boundaries.
- ADR-0003: Keep Electron Behind Backend API Boundary.
- ADR-0006: Use Strongly Typed Domain Identifiers.
