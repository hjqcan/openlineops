# OpenLineOps Production-Line Implementation Baseline

Last updated: 2026-07-11

## Product Contract

OpenLineOps is an IDE and headless runtime platform for third parties to build,
publish, operate, and trace automation production lines. Studio opens a root
`.oloproj`; each independently portable Application owns one `.oloapp` and all
of its topology, layout, production route, Flows, configuration, plugins, and
external-program resources.

The authoring model is project-first and Blockly-first. Python Script remains a
separate advanced Flow node. Runtime never treats Python generated from Blockly
as a second source of truth.

## Formal Boundaries

### Production

Production owns the immutable process definition:

- `ProductModelDefinition`;
- Station-System-bound `OperationDefinition` nodes;
- explicit Station, Fixture, Device, Slot Group, and Slot resources;
- sequential, conditional, bounded-rework, parallel-fork, parallel-join, and
  terminal `RouteTransition` edges;
- exact Action authorization compiled from canonical Flow IR.

A Station is a System. There is no duplicate workstation aggregate and no
ordered test-stage root. An external vendor program is an Application resource
referenced by an ordinary Flow Action.

### Runtime

Runtime owns execution and WIP:

- `ProductionUnit`, `ProductionLot`, `Carrier`, genealogy, material location,
  Slot occupancy, and product disposition;
- `ProductionRun`, `OperationRun`, typed Production Context output, route
  decisions, Incidents, and recovery decisions;
- Station, Fixture, Device, Slot Group, and Slot leases with monotonically
  increasing fencing tokens;
- persisted, rebuildable line, Station, Slot, queue, and active-run projections.

`ExecutionStatus`, `ResultJudgement`, and product disposition are independent.
A vendor-declared nonconforming result is a completed execution with a failed
judgement. Infrastructure or protocol failure has an unknown judgement and an
Incident.

### Coordinator And Agent

The Coordinator is authoritative for route, material, leases, commands, and
projections. PostgreSQL persists production coordination and a transaction
Outbox. RabbitMQ carries Station jobs and safety commands.

Each Station has one Windows Agent identity. The Agent persists its command
Inbox, result Outbox, checkpoints, cancellation decisions, and latest resource
fences in file-backed SQLite. Global idempotency keys prevent redelivery from
replaying hardware. An interrupted non-idempotent action becomes
`RecoveryRequired` and requires an explicit reconcile, retry, abort, or scrap
decision.

Normal work, cancellation, Safe Stop, and Emergency Stop are distinct message
contracts. Emergency Stop uses the independent high-priority Station safety
channel and does not wait behind the normal job queue.

### Immutable Deployment

Publication freezes one strict release shape and creates a signed `.olopkg` for
each Station identity. A package includes the frozen Application content and
all referenced Flow, configuration, plugin, and external-program files. The
Agent verifies the trusted RSA-PSS signature, exact archive membership, every
file hash, Station identity, and package content digest before installing it in
a read-only content-addressed cache.

Station Runtime accepts one strict request/result document protocol. External
program execution uses an isolated working directory, a restricted account and
AppContainer profile, a Windows Job Object with kill-on-close, explicit network
capabilities, bounded CPU/memory/process/output/artifact resources, and complete
process-tree termination. Stdout, stderr, reports, images, CSV, and PDF evidence
are hashed artifacts uploaded to central storage before the public completion
event is acknowledged.

### Trace

Trace preserves immutable evidence for:

- Project, Application, release, line, Production Unit, lot, and genealogy;
- every material location transition and Slot occupancy transition;
- Operation attempts, typed outputs, route decisions, commands, execution
  status, judgement, disposition, Incidents, resource fences, and artifacts;
- local and remote Agent execution through the same strict evidence contract.

Trace read models are projections. They can be rebuilt from persisted Runtime,
material, Station-job, and evidence records.

## Studio And Headless Operation

Before a project is open, Studio presents New, Open, and Recent project entry
actions. After opening, it presents project/application Explorer, hierarchical
2D layout editing, production route graph editing, Blockly/Python Flow editing,
external-program import and protocol trial, configuration, run monitoring, and
Trace.

Edit mode changes Application source. Run mode consumes only an immutable
published snapshot. The shared 2D/3D projection exposes active products,
Station queues, Slot occupancy, resource states, current Operation, and material
flow. Operator commands include Pause, Continue, Stop, Hold, Release, Rework,
Scrap, Safe Stop, recovery decisions, and Emergency Stop.

`OpenLineOps.Runner` starts an immutable published project without opening the
IDE. Production Station computers run `OpenLineOps.Agent` and
`OpenLineOps.StationRuntime` without Studio.

## Public HTTP Surface

- `POST /api/production-units`
- `POST /api/production-units/{id}/arrivals`
- `GET /api/automation-projects/{projectId}/snapshots/{snapshotId}/production-run-context`
- `POST /api/production-runs` with immutable Project Snapshot and Production Unit identities
- `GET /api/production-runs/{id}`
- `POST /api/production-runs/{id}/commands/{command}`
- `GET /api/operations/lines/{lineId}/state`
- `GET /api/operations/active-runs`

The raw production-run injection endpoint is intentionally absent. A run can be
created only from an immutable published Project Snapshot.

## Required Verification

The repository gate is the combination of:

```powershell
dotnet build OpenLineOps.sln --configuration Release --property:TreatWarningsAsErrors=true
dotnet test OpenLineOps.sln --configuration Release --no-build -m:1
powershell -NoProfile -File eng/verify-no-version-suffix-implementations.ps1
powershell -NoProfile -File eng/verify-no-legacy-production-contracts.ps1
powershell -NoProfile -File eng/verify-no-technical-debt-markers.ps1
git diff --check
```

Desktop verification additionally runs strict TypeScript checking, production
bundling, the cold-start production-line projection test, Electron smoke, and a
packaged Windows executable E2E. The packaged E2E covers concurrent Stations,
vendor pass, product nonconformance with rework, cancellation with full process
tree termination, and crash/recovery without hardware replay.

The implementation contains one current schema per owned resource, no format
fallbacks, no legacy execution entry point, and no implementation-generation
suffixes.
