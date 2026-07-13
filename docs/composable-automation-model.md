# Composable Automation Model

Last updated: 2026-07-11

## Product intent

OpenLineOps Studio is an IDE for building and operating automation production
lines. A project may assemble several independently portable Applications. Each
Application owns its Systems, topology, two-dimensional layout, Groups, Slots,
Drivers, flows, scripts, production-line definitions, and configuration.

The primary authoring surfaces are:

1. a semantic top-down layout of the Application;
2. visual Blockly flows that bind directly to domain targets;
3. explicit PythonScript nodes for advanced behavior.

Python is an advanced action implementation, not the hidden representation of
Blockly. Blockly is compiled server-side to immutable Flow IR before a release
can be published.

## Non-negotiable invariants

- One physical or logical System has one `systemId`.
- A Station is a specialized System, never a second object paired with a
  System.
- An Operation, runtime Station, layout shape, Flow target, alarm, and trace
  record all use the same Station `systemId`.
- A SlotGroup belongs to exactly one Station System.
- A Slot belongs to exactly one SlotGroup and the same Station System.
- Design-time topology never stores mutable runtime status or occupancy.
- Runtime status is projected onto the published layout by stable identity.
- `ExecutionStatus` describes whether execution worked;
  `ResultJudgement` describes the product result. They are never collapsed into
  one success flag.
- A product can be nonconforming after technically successful execution.
- Production work in progress is represented by `ProductionUnit`,
  `ProductionLot`, `Carrier`, genealogy, location, and Slot occupancy aggregates.
- Layout geometry is semantic and hierarchical, not a collection of unrelated
  absolute rectangles.
- Runtime executes only an immutable Project Snapshot and its content-addressed
  artifacts. It never falls back to editable source, global repositories, or
  live plugin inventory.
- Removed schemas and target kinds are rejected; there are no compatibility
  readers or aliases.

## Ownership hierarchy

```text
AutomationProject
  Application*
    AutomationTopology
      AutomationSystem*
        child AutomationSystem*
        SlotGroup*
          Slot*
      CapabilityContract*
      DriverBinding*
    SiteLayout*
    ProductionLineDefinition*
    ProcessDefinition*
    PythonScript and Blockly resources
    Engineering configuration
```

The root `.oloproj` composes Applications. An Application directory contains
one `.oloapp` file and all resources owned by that Application. It must remain
portable when copied into another project.

## AutomationSystem

`AutomationSystem` is the canonical structural and behavioral unit. It has:

- `systemId`: immutable, Application-local identity;
- `parentSystemId`: optional containment parent;
- `kind`: `System` or `Station`;
- `systemType`: reusable vendor/domain type such as `Conveyor`, `AxisMotion`,
  `Vision`, `ExternalTester`, or a vendor Station type;
- `displayName`;
- required and provided capability contract ids;
- strict string metadata for extension-owned identification.

`StationSystem` derives from `AutomationSystem`. System kind, rather than
`systemType`, determines whether the Station invariants apply. This lets vendors
define several station types without weakening the common runtime identity.

A general System may be top-level or nested. A Station may contain component
Systems and SlotGroups. A Group or Slot cannot belong to a non-Station System.

## Capability and Driver

A `CapabilityContract` describes what a System can request or provide. It owns
a stable capability id, command name, semantic version, strict input/output
schemas, timeout policy, and safety classification.

A `DriverBinding` resolves one capability contract to one provider. The
provider can be a frozen plugin package, simulator, external program adapter,
or another explicitly supported runtime provider. The binding is design-time
configuration. Publication resolves and locks the exact provider artifact.

A Driver is not a child System and a Capability is not runtime state. Keeping
these concepts separate makes a System replaceable when another provider
satisfies the same contract.

## Group, Slot, and production material

A `SlotGroup` represents a set of material endpoints operated as a unit: a
fixture nest, tester bank, tray row, buffer lane, or robot pick group. It owns a
capacity and belongs to one Station System.

A `Slot` is a stable endpoint in that group. It owns an address, display name,
allowed material kind, and enabled policy. At runtime it can bind exactly one
`ProductionUnit` or `Carrier`. Its state is `Available`, `Reserved`, `Occupied`,
`Running`, `Blocked`, or `Offline`.

A `ProductModelDefinition` declares the Application-local product model and its
identity input key. A `ProductionUnit` is one traceable instance of that model.
It may belong to a `ProductionLot`, travel inside a `Carrier`, occupy a Slot, and
participate in parent/child assembly genealogy. Its disposition is independent
from execution and is `InProcess`, `Completed`, `Nonconforming`, `Held`, or
`Scrapped`.

Slot layout identity must survive rename and movement. Runtime occupancy,
measurements, result judgement, and movement history are separate runtime and
trace projections.

## Hierarchical SiteLayout

A SiteLayout is a semantic view of one AutomationTopology. The 2D editor and
runtime monitor use the same document.

Every element contains:

- `elementId`;
- `kind`: `SystemShape`, `GroupRegion`, or `SlotShape`;
- a target `{ kind, targetId }`;
- `parentElementId`;
- local `x`, `y`, `width`, `height`, and `rotationDegrees`;
- `zIndex` and strict style metadata.

Containment rules are enforced by the backend:

- a top-level element is a SystemShape;
- a nested SystemShape targets a child System of the parent shape;
- a GroupRegion is inside its Station SystemShape and targets a Group owned by
  that Station;
- a SlotShape is inside its GroupRegion and targets a Slot owned by that Group;
- child geometry remains inside its parent.

Coordinates are local to the parent element. Moving a Station therefore moves
its entire visual subtree without a multi-write compensation protocol.

The 3D editor and live view are another projection of the same topology,
parent-local geometry, and runtime state. They support orbit, zoom, selection,
and block dragging while persisting the same layout coordinates as 2D. They do
not create alternative System, Group, or Slot identities.

## ProductionLineDefinition

Production owns the manufacturing/test route definition. It does not own the
physical topology, mutable work in progress, or executable Flow graph.

A line definition contains:

- one `ProductModelDefinition` and runtime identity input key;
- one entry Operation;
- `OperationDefinition` nodes, each referencing exactly one Station `systemId`,
  published Flow, and frozen configuration snapshot;
- `RouteTransition` edges for sequence, result judgement, typed output equality,
  bounded rework, parallel fork, and parallel join;
- optional external-program adapter resources with arguments, input mappings,
  typed result mappings, timeout, and provider/executable declaration.

An external test is still a standard Flow action. It targets the Operation's
Station System and enters the normal command, timeout, monitoring, release lock,
and trace lifecycle. A vendor-reported product failure is
`Completed + Failed`; a crash, invalid protocol, or device fault is
`Failed + Unknown` and creates an Incident. Production cannot launch an
arbitrary program around Runtime.

## ProcessDefinition and Flow IR

Blockly and PythonScript are distinct process node kinds:

- Blockly stores only a current Blockly workspace.
- PythonScript stores only explicit Python source.
- Command, Decision, Delay, Start, and End remain explicit graph nodes.

Every Blockly block version has a canonical runtime action contract and hash.
Publication compiles the workspace to versioned Flow IR with exact block id,
block type/version, target kind/id, capability, command, input, timeout, and
retry source mapping.

Valid domain target kinds are `System`, `Capability`, `Driver`, `SlotGroup`,
`Slot`, and `ProductionUnit`. There is no generated Python shadow source and no runtime
`automation_plan` expansion.

A PythonScript action is published as an explicit, bounded Flow IR action. It
cannot return new actions or commands that bypass release-time validation.

## Edit, publish, and run

### Edit

Studio edits Application-owned resources. The layout is freely draggable while
respecting semantic containment. Draft files contain no live runtime state.

### Publish

Publisher validates and freezes:

- the Application manifest;
- topology and hierarchical layouts;
- production line definitions;
- process graphs, Blockly workspaces, and Python source;
- canonical Flow IR and source maps;
- capability and Driver bindings;
- exact provider package trees and content hashes;
- runtime target references.

Any missing target, provider, block contract, executable, or hash fails the
publication.

### Run

Runtime accepts a published Project Snapshot only. The Station `systemId` is the
runtime station identity. Every action enters one standard execution lifecycle
and produces monitoring and trace evidence.

Creating a `ProductionRun` is asynchronous. The Coordinator persists the Run
before dispatch, advances its route from typed Operation outputs and judgements,
and leases Station, Slot, Fixture, and Device resources with fencing tokens.
There is no project-wide execution lock: one unit can run at a downstream
Station while another enters an upstream Station.

The layout monitor overlays runtime projections without changing layout files:

| State | Meaning | Default visual |
|---|---|---|
| Idle | Available and not executing | Green |
| Running | Session/action in progress | Yellow |
| Completed | Latest execution completed | Green with completion mark |
| Failed | Latest execution failed or has an unhandled incident | Red |
| Offline | Runtime/provider heartbeat unavailable | Gray |

Target-level command state may additionally color a nested System, Group, or
Slot when the action explicitly targets it.

## Composition example

```text
Application: Mainboard Line
  StationSystem: Load And Inspect
    System: Barcode Reader
    System: Vision Camera
    SlotGroup: Input Nest
      Slot: L1
      Slot: L2
  StationSystem: Functional Test
    System: External Tester Adapter
    SlotGroup: Tester Bank
      Slot: T1
      Slot: T2
      Slot: T3
      Slot: T4
```

The two Stations can be moved independently on the Application canvas. Moving
Functional Test carries Tester Bank and T1-T4. A route can connect an Inspect
Operation to a Functional Test Operation, then branch on judgement to Complete,
bounded Rework, or Hold. Blockly targets their Systems/Groups/Slots and Runtime
paints the same shapes, product locations, queues, and Slot occupancies from the
published execution projection.
