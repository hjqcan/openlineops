# ADR-0006: Use Strongly Typed Domain Identifiers

## Status

Accepted

## Date

2026-06-30

## Context

OpenLineOps has bounded contexts for processes, runtime, devices, engineering,
traceability, and plugins. These contexts contain identifiers with different
business meanings even when the underlying storage representation is often a
`Guid` or `string`.

The local `lib/NetDevPack` library and OpenLineOps modular DDD conventions provide
useful DDD contracts, repository abstractions, Unit of Work semantics, and
domain event infrastructure. NetDevPack's concrete `Entity` base uses a `Guid`
identifier, but replacing OpenLineOps identifiers with naked `Guid` values
would weaken context boundaries.

## Decision Drivers

- Prevent accidentally passing a device identifier where a process, recipe, or
  runtime-session identifier is required.
- Keep ubiquitous language visible in domain models and public contracts.
- Preserve compatibility with NetDevPack repository and Unit of Work contracts.
- Avoid scattering ad hoc identifier conversion code across EF, API DTOs,
  plugin manifests, and desktop contracts.

## Considered Options

### Option 1: Keep strongly typed IDs in the domain

- Pros: Best domain clarity, compile-time safety, explicit bounded-context
  language, compatible with future plugin and traceability contracts.
- Cons: Requires consistent persistence, serialization, and mapping support.

### Option 2: Replace all aggregate IDs with `Guid`

- Pros: Simplest EF mapping and direct compatibility with NetDevPack's concrete
  `Entity` base.
- Cons: Loses domain meaning, makes cross-context ID mixups easier, and weakens
  the product's DDD model.

### Option 3: Use `string` everywhere

- Pros: Easy JSON, plugin manifest, and CLI integration.
- Cons: Weakest compile-time guarantees and pushes validation into scattered
  runtime checks.

## Decision

OpenLineOps domain aggregates will keep strongly typed identifiers such as
`ProcessDefinitionId`, `DeviceInstanceId`, `RecipeId`, and
`RuntimeSessionId`.

The project will align with NetDevPack through contracts instead of adopting
NetDevPack's concrete `Guid Id` entity base for OpenLineOps aggregates. Domain
aggregate roots implement NetDevPack's `IAggregateRoot`, repositories expose
NetDevPack Unit of Work semantics, and infrastructure provides centralized
identifier conversion for EF Core.

## Rationale

The product is not simple CRUD. It coordinates device commands, visual process
flows, runtime execution, engineering configuration versions, and traceability.
In that model, identifier meaning matters. Strongly typed IDs make invalid
cross-context calls harder to write and easier to review.

The cost is real but manageable when conversion is centralized. The
`OpenLineOps.Infrastructure.Data.Core` package provides a reusable EF Core
conversion helper and base repository pattern so each bounded context does not
hand-roll its own ID adapter.

## Consequences

### Positive

- Domain code remains explicit and safer across bounded contexts.
- API, plugin, and traceability contracts can expose stable string/Guid
  representations without weakening the domain model.
- NetDevPack remains useful as a DDD/CQRS/Unit of Work foundation.
- Future EF-backed contexts can reuse the same strongly typed ID conversion
  pattern.

### Negative

- EF mappings must configure conversions for strongly typed ID properties.
- DTO and plugin manifest mappings still need explicit boundary conversion.
- Contributors must understand why the domain does not inherit NetDevPack's
  concrete `Entity` base.

### Risks And Mitigations

- Risk: Each bounded context implements custom ID conversion differently.
- Mitigation: Use `OpenLineOps.Infrastructure.Data.Core` conversion helpers and
  document the pattern in module templates.

- Risk: Strongly typed IDs become boilerplate without business value.
- Mitigation: Keep IDs context-specific only for aggregate and contract
  boundaries where mixups are plausible.

## Implementation Notes

- Domain aggregates inherit from `AggregateRoot<TId>`.
- `AggregateRoot<TId>` implements `NetDevPack.Domain.IAggregateRoot`.
- EF mappings should use
  `HasStronglyTypedIdConversion<TId, TValue>()` from
  `OpenLineOps.Infrastructure.Data.Core`.
- External contracts may serialize IDs as `Guid` or `string`, but conversion
  should happen at API/application/plugin boundaries.

## Related Decisions

- ADR-0002: Enforce DDD Layering And Boundaries.
- Development plan: NetDevPack DDD foundation slice and Data.Core foundation
  slice.
