# ADR-0002: Enforce DDD Layering And Boundaries

## Status

Accepted

## Date

2026-06-29

## Context

OpenLineOps manages long-lived production-line concepts: process definitions,
runtime sessions, engineering recipes, station profiles, device capabilities,
trace records, and plugin packages. These concepts have business rules that
should remain stable even if the UI, persistence, and device integrations
change.

OpenLineOps is developed as a ground-up original platform. Architecture
decisions should be expressed in terms of OpenLineOps domain requirements and
bounded contexts.

## Decision Drivers

- Keep domain rules independent from ASP.NET Core, Electron, databases, and
  device SDKs.
- Keep use cases testable without real devices or real storage.
- Make context boundaries clear for contributors.
- Prevent controllers and infrastructure adapters from owning domain decisions.

## Considered Options

### Option 1: DDD tactical layering per bounded context

- Pros: Rich domain model, explicit aggregates, testable application services,
  clear dependency direction.
- Cons: More projects and DTO mapping than a simple CRUD structure.

### Option 2: Transaction script services

- Pros: Faster for simple CRUD workflows.
- Cons: Domain rules tend to spread across services, controllers, and database
  code as the product grows.

### Option 3: Database-first model

- Pros: Quick persistence scaffolding.
- Cons: Persistence concerns drive the domain model and make runtime behavior
  harder to test independently.

## Decision

Each bounded context will follow DDD-oriented layering:

- **Domain** contains aggregates, entities, value objects, domain events, and
  domain validation.
- **Application** contains use cases, ports, DTOs, result handling, and mapping
  across context boundaries.
- **Infrastructure** implements persistence, time, device adapters, plugin
  adapters, and other external dependencies.
- **API** exposes HTTP/SignalR contracts and translates application results into
  protocol responses.

Dependencies point inward. Domain projects do not reference Application,
Infrastructure, API, ASP.NET Core, Electron, or database SDKs.

## Rationale

The product domain is behavior-heavy: runtime sessions transition through
states, process definitions must be validated before publication, and future
engineering configuration must be immutable and traceable. These are better
expressed through domain behavior and application use cases than through CRUD
controllers or database-first models.

## Consequences

### Positive

- Business rules can be tested without HTTP, storage, or devices.
- Application use cases provide stable seams for Electron, API, and automation.
- Bounded contexts can evolve without exposing internal persistence models.
- Domain events can later support audit, projections, and integration flows.

### Negative

- More boilerplate: DTOs, mappers, ports, and module registration.
- Contributors need to understand which layer owns each responsibility.
- Some simple CRUD features may feel heavier than necessary.

### Risks And Mitigations

- Risk: Anemic domain model where domain projects only contain data.
- Mitigation: Put lifecycle transitions, invariants, publication rules, and
  validation behavior in aggregates/domain services.

- Risk: Controllers accumulate business logic.
- Mitigation: Controllers must delegate to application services and only handle
  HTTP validation, DTO mapping, and status codes.

## Implementation Notes

- Keep application services behind interfaces when consumed by API modules.
- Use result objects for expected business failures instead of exceptions for
  normal control flow.
- Keep domain identifiers context-specific unless a stable shared contract is
  required.
- Use infrastructure adapters for in-memory development implementations first,
  then replace with SQLite/PostgreSQL adapters behind the same ports.

## Related Decisions

- ADR-0001: Use Modular Monolith First.
- ADR-0004: Define Explicit Plugin Contract And Lifecycle.
