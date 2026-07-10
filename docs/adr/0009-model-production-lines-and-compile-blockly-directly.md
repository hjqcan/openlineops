# 0009. Model production lines separately and compile Blockly directly

- Status: Accepted
- Date: 2026-07-10
- Supersedes: [ADR 0007](0007-make-automation-project-workspace-primary-product-shell.md)
- Topology identity portion superseded by: [ADR 0010](0010-make-system-canonical-and-layout-hierarchical.md)

## Context

An OpenLineOps Application must describe more than one executable flow. A production line composes a DUT model, topology-bound workstations, ordered stages, authored automation flows, and optional external test programs. Treating that composition as another `ProcessDefinition` would mix manufacturing semantics with the executable graph model and make stage routing, portable vendor programs, and DUT identity implicit.

Blockly was also represented as a mode of a Python script node. The desktop generated Python from Blockly, persisted both representations, and allowed custom blocks whose only execution contract was a Python template. That made Python the actual source of truth, prevented exact static action analysis, and allowed actions to bypass release-time dependency locking.

## Decision

Introduce `OpenLineOps.Production` as an independent bounded context with Domain, Application, Infrastructure, and API modules.

A `ProductionLineDefinition` is an Application-owned portable resource stored at `production/lines/<line-id>/line.json`. It contains:

- one DUT model and runtime identity input key;
- workstations that reference one canonical Station System id;
- contiguous ordered stages that reference published executable flows;
- external test program adapters with Application-relative executable or exact provider binding, arguments, DUT input mappings, result mappings, and timeout.

External test execution is never a side channel. Its stage flow must compile to exactly one matching device-command action targeted at the workstation Station System. The command then uses the same runtime command, monitoring, trace, and release dependency lifecycle as authored automation.

Make `Blockly` and `PythonScript` separate process node kinds. A Blockly node stores only the current Blockly workspace. A Python node stores only Python source. Remove script-editor modes, generated-Python persistence, legacy Python block templates, and all compatibility readers.

Compile Blockly server-side into versioned Flow IR. Every declarative block has a canonical runtime action contract, content hash, explicit target kind and target id where applicable, and a source block id. Publication validates targets, freezes exact block versions and contract hashes, and locks referenced provider packages by immutable content hash. Runtime resolves only the frozen release artifacts.

## Consequences

- Production-line composition is reusable, portable with its `.oloapp`, and independent of the physical topology aggregate and executable graph aggregate.
- Blockly is the primary statically analyzable authoring surface; Python remains an explicit advanced escape hatch.
- External vendor programs and OpenLineOps-authored tests share one command lifecycle and trace model.
- Releases can fail closed before runtime when a target, block contract, provider package, executable, or content hash is missing or inconsistent.
- Existing Blockly-as-Python resources, legacy custom block documents, and older release artifacts are rejected. No migration or compatibility path is provided.
