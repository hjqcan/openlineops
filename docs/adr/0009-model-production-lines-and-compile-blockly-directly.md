# ADR-0009: Model Production Lines Separately And Compile Blockly Directly

## Status

Accepted

The distributed execution and deployment portions are refined by ADR-0011.
The topology identity portion is refined by ADR-0010.

## Date

2026-07-10

## Context

An OpenLineOps Application describes more than one executable Flow. A production
line composes a Product Model, topology-bound Station Systems, routed Operations,
Application resources, and frozen execution dependencies. Treating that
composition as another process definition would mix production semantics with
the executable Flow graph and make routing, portable vendor programs, material
identity, and resource ownership implicit.

Blockly was also represented as a mode of a Python script node. The desktop
generated Python from Blockly, persisted both representations, and allowed
custom blocks whose only execution contract was a Python template. That made
Python the actual source of truth, prevented exact static action analysis, and
allowed actions to bypass release-time dependency locking.

The product has not been released, so the formal model replaces the removed
test-oriented roots directly. There are no old readers, migrations, or parallel
contract generations.

## Decision

Keep `OpenLineOps.Production` as an independent bounded context with Domain,
Application, Infrastructure, and API modules.

A `ProductionLineDefinition` is an Application-owned portable resource stored
under `production/lines/<line-id>/line.json`. It contains:

- one `ProductModelDefinition` with its runtime identity input key;
- graph-routed `OperationDefinition` values, each bound to one canonical
  Station System and one frozen executable Flow;
- explicit Station, Fixture, Device, Slot Group, or Slot resource bindings;
- typed conditional, bounded rework, parallel fork, parallel join, and terminal
  route transitions;
- Application-owned external program resources with a frozen executable or
  exact Provider binding, typed input and result mappings, permissions,
  execution limits, and content hashes.

An external program is never a special production-operation field. Its Flow
contains an ordinary runtime Action that references the Application resource.
The Action uses the same authorization, command, fencing, monitoring, evidence,
and release dependency lifecycle as every other automation Action.

Make Blockly and Python Script separate process node kinds. A Blockly node
stores only its Blockly workspace. A Python node stores only Python source.
Blockly is compiled server-side into canonical Flow IR. Every declarative block
has a stable runtime Action contract, content hash, explicit target kind and
target identity, and source block identity. Publication validates the complete
Action target against the Operation Station scope and resources, freezes its
exact command authorization, and locks referenced Provider packages by immutable
content hash. Runtime resolves only the frozen release artifacts.

## Consequences

- Production-line composition is portable with its `.oloapp` and independent of
  topology authoring and Flow authoring aggregates.
- A Station is a System; an Operation references that System rather than a
  duplicate workstation concept.
- Blockly remains the primary statically analyzable authoring surface, while
  Python remains an explicit advanced extension surface.
- External vendor programs and OpenLineOps-authored actions share one command
  lifecycle and one production Trace model.
- Publication fails closed when a target, resource, Action authorization,
  Provider package, executable, hash, route, or Station boundary is invalid.
- Generated-Python persistence, Python-backed custom block templates, ordered
  test-stage roots, and mutable release dependencies are not part of the model.

## Related Decisions

- ADR-0008: Use Portable Application Project Units.
- ADR-0010: Make System Canonical And Layout Hierarchical.
- ADR-0011: Coordinate Production Through Station Agents.
