# 0010. Make System canonical and layout hierarchical

- Status: Accepted
- Date: 2026-07-10

## Context

The topology model represented a workstation with two identities: an
`EquipmentNode` described its structural position while an `AutomationModule`
described its behavior. Production workstations therefore referenced both
objects, Blockly targets had to choose between both target kinds, and runtime
station status had no single identity that could be projected onto the layout.

The first two-dimensional layout also stored every element in absolute canvas
coordinates. A station, its groups, and its slots looked nested but were not a
semantic container tree. Moving a station could leave its children behind, or
require several independent writes that could fail halfway through.

OpenLineOps users work with `Application`, `System`, `Driver`, `Group`, and
`Slot` as composable concepts. A station is a specialized System, and the same
station identity must be used by authoring, production composition, execution,
monitoring, and traceability.

## Decision

Make `AutomationSystem` the only structural and behavioral system identity in
an Application topology. `Station` is a strict System kind and is represented
as a station-specific System subtype in the domain. A System has:

- one stable `systemId`;
- an optional `parentSystemId`;
- a strict kind, a reusable `systemType`, and a display name;
- required and provided capability contract ids;
- immutable extension metadata.

Delete `EquipmentNode`, `AutomationModule`, their identifiers, their API target
kinds, and their persisted fields. No aliases or compatibility readers are
provided. Capability contracts and driver bindings remain separate because
they describe behavior contracts and provider resolution rather than physical
containment.

A `SlotGroup` belongs to one Station System. A `Slot` belongs to one group and
the same Station System. A Production `WorkstationDefinition` references only
`stationSystemId`. Flow IR targets use `System`, `Capability`, `Driver`,
`SlotGroup`, `Slot`, or `Dut`; an external-test action targets the workstation's
Station System.

Replace the flat layout document with a strict hierarchical layout:

- every element has `parentElementId` and a semantic target object;
- a top-level element is a `SystemShape`;
- a child System or `GroupRegion` is positioned in a Station `SystemShape`;
- a `SlotShape` is positioned in its corresponding `GroupRegion`;
- `x` and `y` are local to the parent element;
- child geometry must remain inside its parent and the target hierarchy must
  match the topology hierarchy.

The 2D editor and runtime monitor render this same immutable semantic layout.
Runtime state is a separate projection keyed by `systemId` and never mutates
the design document. The standard visual states are Idle, Running, Completed,
Failed, and Offline. A future 3D renderer must project the same System/Group/
Slot identities and runtime state; it must not introduce another topology
model.

Topology and layout persistence use their single current schema. Published
releases freeze the exact topology, hierarchical layout,
processes, bindings, and provider artifacts used by Runtime.

## Consequences

- A workstation has one identity from the Application canvas through runtime
  monitoring and trace evidence.
- Moving a Station automatically carries its nested Systems, groups, and slots
  because child coordinates are local to the container.
- Layout writes cannot produce a visually nested but semantically unrelated
  structure.
- Edit mode and monitor mode cannot drift because both use the same published
  layout and differ only by the runtime-state overlay.
- Third-party systems remain replaceable through capability contracts and
  frozen driver/provider bindings.
- Topology, layout, production-line, and Flow IR resources have one canonical
  System identity model and no compatibility representations.
