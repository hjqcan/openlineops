# ADR-0011: Coordinate Production Through Station Agents

## Status

Accepted

Supersedes the runtime deployment portion of ADR-0001, ADR-0005, and ADR-0009.

## Date

2026-07-11

## Context

OpenLineOps is an IDE and runtime for third parties to assemble automation
production lines. A line may manufacture, inspect, test, rework, isolate, or
scrap a product across independently deployed stations. Multiple products must
move concurrently, and a vendor test result that says a product is
nonconforming must not be confused with a process or infrastructure failure.

The former local runtime represented one DUT moving through contiguous stages
and executed a complete route synchronously inside the Studio backend. That
model could not safely express WIP location, Slot ownership, Carrier contents,
conditional routes, station isolation, offline execution, or non-idempotent
hardware recovery.

The product is unreleased. There is no compatibility promise and retaining old
readers or implementation suffixes would create permanent ambiguity.

## Decision Drivers

- Run multiple products concurrently without allowing conflicting hardware use.
- Keep each Application portable between projects without rewriting files.
- Make route, material, result, incident, and artifact evidence reconstructable.
- Continue station work during a temporary Coordinator outage and submit the
  result exactly once after reconnection.
- Fail closed when package content, station authority, or resource ownership is
  not proven.
- Keep Production, Runtime, Agent, and Trace responsibilities explicit.

## Considered Options

### Keep synchronous execution in the Studio backend

- Pros: one process, simple local debugging.
- Cons: no station fault boundary, no offline execution, line-wide locking,
  poor recovery semantics, and the IDE must remain open.

### Let every station coordinate its own downstream route

- Pros: no central service is required.
- Cons: conflicting route decisions, fragmented WIP truth, difficult joins and
  rework, and no authoritative material genealogy.

### Central Coordinator with one persistent Agent identity per station

- Pros: one authoritative route and WIP model, isolated station execution,
  local durable checkpoints, explicit leases, and independent deployment.
- Cons: introduces broker, database, package trust, and distributed recovery
  operations.

## Decision

Use a central Coordinator for route decisions and material state, and run one
Windows Agent identity for each Station System.

Production defines `ProductModelDefinition`, `OperationDefinition`, and a graph
of `RouteTransition` values. Runtime owns `ProductionUnit`, lot, Carrier,
genealogy, location, Slot occupancy, operation attempts, route decisions,
resource leases, and production-run control. Agent owns station command Inbox,
result Outbox, execution checkpoints, package verification, and device/provider
adapters. Trace stores the resulting evidence and artifacts.

Each execution result has independent axes:

- `ExecutionStatus` describes whether the command machinery completed.
- `ResultJudgement` describes the product or operation outcome.

A vendor-reported nonconformance is `Completed + Failed`. A crash, invalid
protocol, timeout, or device error is an execution failure with `Unknown`
judgement and an Incident. Product disposition is independent again.

The Coordinator stores WIP, routes, leases, commands, and rebuildable
projections in PostgreSQL. Its transaction Outbox publishes station jobs via
RabbitMQ. Each Agent durably stores its Inbox, result Outbox, and checkpoints in
file-backed SQLite and deduplicates every command by a global idempotency key.

Resources are protected by scoped leases with monotonically increasing fencing
tokens. A process that restarts while a non-idempotent hardware action is
running enters `RecoveryRequired`; it is never automatically replayed.

Deployment uses signed `.olopkg` files. A package contains one frozen
Application and all referenced flows, configurations, plugins, and vendor
programs. The Agent accepts exactly one strict manifest shape, verifies the
trusted signature and every file hash, rejects unlisted content and unsafe
paths, and installs into a read-only content-addressed cache.

The release adapter publishes one Station-bound package per Station and a
strict deployment catalog. Physical routing is configured once by
Project/Application/Station; Snapshot identity remains dynamic catalog data.
Package signatures use only RSA-PSS-SHA256 with RSA keys of at least 3072 bits.
Production signing keys are externally provisioned and must remain outside all
Project and package-content roots. Packaged local Studio provisions its own
user-data signing identity explicitly; this is not a Production fallback.

The IDE and public API expose only the current formal contract. API Explorer
groups and Flow IR names use stable capability names without implementation
generation suffixes. Old DUT, Workstation, Stage, synchronous line execution,
and format readers are deleted rather than migrated.

## Rationale

Central coordination is required for graph joins, rework, genealogy, and
authoritative WIP, while station Agents are required for hardware fault
isolation and offline result durability. Explicit leases and idempotency address
the two distributed-system risks that matter on a physical line: duplicate
motion and simultaneous ownership. Separate execution, judgement, and
disposition axes preserve the business distinction between a bad product and a
broken system.

## Consequences

### Positive

- Products can pipeline across stations and Slots without a project-wide lock.
- Studio can disconnect or restart without owning physical execution.
- An Application remains the portable unit of authoring and deployment.
- Operators can see current WIP and reconstruct every decision from evidence.
- Vendor programs and plugins execute only from verified immutable content.

### Negative

- Production deployments require PostgreSQL, RabbitMQ, Agent lifecycle
  management, certificates, trusted signing keys, and clock discipline.
- Cross-process failures require explicit operator recovery workflows.
- Route and parallel-join validation is more complex than ordered stages.

### Risks And Mitigations

- Risk: a stale process continues acting after losing ownership.
  Mitigation: every device-affecting command carries a fencing token that the
  station checks against its latest lease.
- Risk: broker redelivery repeats hardware work.
  Mitigation: persist Inbox acceptance and execution checkpoints before action;
  deduplicate by global idempotency key.
- Risk: package or vendor content is replaced after publication.
  Mitigation: trusted signature, per-file hashes, exact archive membership, and
  a read-only content-addressed cache.
- Risk: Coordinator projections diverge after restart.
  Mitigation: rebuild them from persisted command, material, route, lease, and
  result records.

## Implementation Notes

- Commands return after durable submission; HTTP creation responds with
  `202 Accepted` and a production-run identifier.
- Normal stop and emergency stop are separate channels. Emergency stop is a
  station safety operation and does not depend on the normal job queue.
- A Flow may control only its current Station System subtree unless a signed
  line-controller capability explicitly grants a larger scope.
- External programs run in isolated working directories with bounded resources,
  complete process-tree termination, and hashed stdout, stderr, and report
  artifacts.
- 2D and 3D topology views consume the same persisted runtime projection.

## Related Decisions

- ADR-0002: Enforce DDD Layering And Boundaries.
- ADR-0008: Use Portable Application Project Units.
- ADR-0010: Make System Canonical And Layout Hierarchical.
