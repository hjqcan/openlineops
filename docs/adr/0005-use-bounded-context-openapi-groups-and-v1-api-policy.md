# ADR-0005: Use Bounded Context OpenAPI Groups And V1 API Policy

## Status

Accepted

## Date

2026-06-29

## Context

OpenLineOps exposes APIs from multiple bounded contexts through one ASP.NET
Core host. Current APIs cover project/application authoring, engineering,
production, runtime, devices, traces, and plugins.

The backend is consumed by Electron locally and may also be deployed as a
server-side API. API contracts need to remain discoverable and stable while the
pre-1.0 platform is still evolving quickly.

## Decision Drivers

- Keep API documentation grouped by bounded context.
- Keep one correct current contract while the product is unreleased; breaking
  clean cutovers are preferred to compatibility layers.
- Make the current public API generation explicit as `v1`.
- Keep versioning policy lightweight until external compatibility pressure
  requires full URL or media-type versioning.
- Keep API metadata reusable by host and module API projects.

## Considered Options

### Option 1: Bounded-context OpenAPI groups with explicit v1 metadata

- Pros: Low ceremony, matches modular monolith boundaries, keeps existing
  routes stable, easy to test through API Explorer metadata.
- Cons: Does not yet provide parallel major versions in route templates.

### Option 2: URL version every route immediately

- Pros: Strong visible version boundary from day one.
- Cons: Adds churn to early routes and tests before external clients exist.

### Option 3: No version or group policy until later

- Pros: No upfront work.
- Cons: OpenAPI output becomes harder to navigate as modules grow, and
  versioning decisions become inconsistent.

## Decision

OpenLineOps will use bounded-context OpenAPI groups named
`{bounded-context}-v1`, with the current document version named `v1`.

Routes use the current `/api/...` shape during the unreleased platform phase.
Contract changes replace that surface atomically across backend, desktop,
tests, and documentation. Parallel public contracts require a new decision
after an actual compatibility promise exists.

API metadata constants live in `shared/OpenLineOps.Api.Abstractions` so module
API projects and tests use the same group and version names.

## Rationale

The current priority is clear API discovery and repeatable integration tests,
not full public API lifecycle management. Bounded-context groups align with the
modular monolith structure and make the OpenAPI surface easier to navigate as
new modules arrive.

## Consequences

### Positive

- API Explorer groups match platform, process, runtime, and future bounded
  contexts.
- Backend, desktop, tests, and documentation share one current route surface.
- The current OpenAPI document has an explicit `v1` name.
- Group/version names can be tested through shared constants.

### Negative

- Route URLs do not visibly include `/v1`.
- Any post-release parallel contract will require an explicit ADR and route
  strategy.

### Risks And Mitigations

- Risk: Contributors add controllers without group metadata.
- Mitigation: Keep group constants in `OpenLineOps.Api.Abstractions` and add API
  metadata tests as controller count grows.

- Risk: An unreleased route is accidentally treated as a compatibility promise.
- Mitigation: Permit atomic breaking changes before release and document a
  public compatibility promise only when the product actually makes one.

## Implementation Notes

- Use `ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.<Context>V1)` on
  controller classes.
- Use `OpenLineOpsApiRoutes` constants for current route templates.
- Use `OpenLineOpsApiVersions.Current` when registering the OpenAPI document.
- Keep health endpoints in a health-specific group.

## Related Decisions

- ADR-0001: Use Modular Monolith First.
- ADR-0002: Enforce DDD Layering And Boundaries.
- ADR-0003: Keep Electron Behind Backend API Boundary.
