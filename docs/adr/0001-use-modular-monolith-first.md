# ADR-0001: Use Modular Monolith First

## Status

Accepted

## Date

2026-06-29

## Context

OpenLineOps is a ground-up original automated test production-line runtime platform.
The product must cover engineering configuration, device integration,
process orchestration, runtime monitoring, data traceability, and plugin-based
delivery.

The early product has many bounded contexts but limited production evidence
about deployment topology, scaling pressure, and operational ownership. The
system must run well as a local desktop-backed backend for Electron and remain
deployable as a server-side API later.

## Decision Drivers

- Keep bounded contexts explicit without introducing distributed-system
  complexity before domain boundaries stabilize.
- Support fast local development and desktop deployment.
- Preserve a migration path to separately deployed services if operational
  evidence later justifies the split.
- Keep cross-context transactions and testability manageable during early platform development.

## Considered Options

### Option 1: Modular monolith

- Pros: Clear module boundaries, simple deployment, simple local development,
  easier transactional consistency, low operational burden.
- Cons: Requires discipline to prevent modules from becoming tightly coupled.

### Option 2: Microservices from the start

- Pros: Independent deployment and scaling per service from day one.
- Cons: Premature service boundaries, network failure handling, distributed
  transactions, more infrastructure, slower local development.

### Option 3: Single-layer monolith

- Pros: Fastest initial coding path.
- Cons: Weak domain boundaries, difficult extraction, high long-term coupling.

## Decision

OpenLineOps will start as a **modular monolith**. Each bounded context is
implemented as a set of module projects under `modules/`, with separate
Domain, Application, Infrastructure, and API projects where needed. The host in
`src/OpenLineOps.Api` composes the modules.

## Rationale

The platform needs strong architectural boundaries but does not yet need the
runtime and operational costs of distributed services. A modular monolith lets
the project model bounded contexts explicitly while keeping local startup,
integration tests, and Electron-backed desktop usage simple.

## Consequences

### Positive

- Modules can be developed and tested independently inside one solution.
- Local desktop and server API deployments share one host model.
- Cross-context use cases can be implemented through application interfaces
  before remote contracts are justified.
- Future service extraction remains possible when boundaries are proven.

### Negative

- Module boundaries are enforced by project references and review discipline,
  not by network isolation.
- A single host process means one failed module can affect the whole backend.
- Build times may grow as module count increases.

### Risks And Mitigations

- Risk: Modules bypass application contracts and directly depend on each
  other's infrastructure.
- Mitigation: Keep project references inward-facing, review dependencies during
  build changes, and add architecture tests when the module graph grows.

- Risk: Runtime concerns leak into engineering configuration or plugin models.
- Mitigation: Add narrow application ports and mapping services at context
  boundaries.

## Implementation Notes

- Use `shared/` only for stable abstractions and contracts.
- Put product capabilities under `modules/`.
- Keep `src/OpenLineOps.Api` as composition root and host shell.
- Avoid cross-module infrastructure references.
- Prefer adding a module API/port over sharing internal domain entities across
  contexts.

## Related Decisions

- ADR-0002: Enforce DDD Layering And Boundaries.
- ADR-0003: Keep Electron Behind Backend API Boundary.
