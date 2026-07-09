# ADR-0003: Keep Electron Behind Backend API Boundary

## Status

Accepted

## Date

2026-06-29

## Context

OpenLineOps will use Electron for the desktop user experience and .NET 10 for
the backend. The backend must also remain deployable without Electron for
server-side scenarios and automated integration tests.

The product includes runtime monitoring, process authoring, engineering
configuration, plugin management, and traceability. These workflows need stable
contracts and should not depend on UI implementation details.

## Decision Drivers

- Keep backend domain and application logic reusable outside Electron.
- Keep Electron replaceable or optional for server deployments.
- Avoid direct database or in-process domain access from the UI shell.
- Support automated API tests as the primary backend verification path.
- Preserve stable contracts for future web clients or command-line tooling.

## Considered Options

### Option 1: Electron communicates through backend APIs

- Pros: Stable contract boundary, reusable backend, clear security and
  validation path, easy integration testing.
- Cons: Requires local backend process management and HTTP/SignalR client code.

### Option 2: Electron directly loads .NET assemblies or calls in-process APIs

- Pros: Lower local call overhead.
- Cons: Tight coupling to backend internals, difficult deployment separation,
  weak contract discipline.

### Option 3: Electron directly accesses local database files

- Pros: Simple for read-only screens in early UI experiments.
- Cons: Bypasses domain rules, creates migration and concurrency risks, makes
  server deployment harder.

## Decision

Electron will communicate with the backend only through stable backend
contracts: HTTP APIs initially, SignalR for realtime runtime monitoring when
needed, and possibly gRPC for high-frequency local service communication only
after evidence shows HTTP/SignalR is insufficient.

Electron must not directly access database files, domain assemblies, or
infrastructure adapters.

## Rationale

The backend is the product platform, not only a desktop helper. API boundaries
make validation, authorization, diagnostics, and future deployment models more
consistent. This also lets integration tests verify real product workflows
without launching Electron.

## Consequences

### Positive

- Electron and backend can evolve independently.
- Backend workflows remain testable through `WebApplicationFactory`.
- Server deployment and desktop deployment share the same application use cases.
- Realtime features can be added deliberately through SignalR contracts.

### Negative

- Desktop packaging must start and supervise the backend process.
- Local API authentication and port management must be designed.
- UI state must handle network-like failures even when backend runs locally.

### Risks And Mitigations

- Risk: Local desktop startup becomes fragile if Electron and backend lifecycle
  are not coordinated.
- Mitigation: Add a desktop process supervisor and health-check handshake before
  building Electron screens.

- Risk: API calls feel too chatty for runtime monitoring.
- Mitigation: Use coarse command APIs for mutations and SignalR projections for
  realtime updates.

## Implementation Notes

- Backend routes are the source of truth for Electron integration.
- Keep `/health/live` and `/health/ready` available for desktop supervision.
- Do not add Electron-specific services into Domain or Application projects.
- Add API contract tests before building Electron screens that depend on them.

## Related Decisions

- ADR-0001: Use Modular Monolith First.
- ADR-0002: Enforce DDD Layering And Boundaries.
