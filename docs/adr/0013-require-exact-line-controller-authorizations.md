# ADR-0013: Require Exact Line Controller Authorizations

## Status

Accepted

Refines the Station ownership and lease policy in ADR-0011.

## Date

2026-07-11

## Context

An Operation normally executes inside one Station System subtree. Some lines
also need a local controller to coordinate a device owned by another Station.
A broad cross-Station flag would let a Flow reach unrelated equipment and
would make a release difficult to audit. Executing the remote provider inside
the local Agent would also violate Station identity and deployment boundaries.

## Decision Drivers

- Keep Station subtree ownership as the default.
- Make every cross-Station action reviewable before publication.
- Disambiguate providers when several child Systems expose the same
  capability.
- Fence both the local controller and remote physical resource.
- Preserve immutable authorization evidence in signed Station packages.

## Considered Options

### Allow any target when an Operation is marked as a Line Controller

- Pros: small model and editor surface.
- Cons: excessive privilege, ambiguous routing, and no exact audit evidence.

### Dispatch the remote provider directly from the local Station Agent

- Pros: reuses ordinary device routing.
- Cons: runs a provider under the wrong Station identity and deployment.

### Route through an exact local controller grant

- Pros: least privilege, deterministic provider selection, and preserved
  Station identity.
- Cons: requires an explicit grant and remote leases for every action.

## Decision

Every Flow action remains confined to its Operation's Station subtree unless
one `LineControllerAuthorization` binds that exact Operation and action id.
The authorization declares:

- local controller owner System, Binding, Capability, and Action;
- remote target Station, System, Binding, Capability, and Action.

The Flow action uses the local controller Capability and Action and names the
remote Driver Binding as its target. Publication verifies the controller is
inside the Operation Station subtree, the controller Binding is a fixed Device
resource, the remote Binding belongs to the declared different Station, and
both Capability/Action pairs match topology contracts.

The immutable release links the authorized Flow action to the authorization
id and freezes the complete tuple. Runtime resolves only the frozen local
controller Binding. It passes the exact remote tuple to that controller and
requires current fencing leases for the Operation Station, controller Binding,
remote Station, and remote Binding before provider invocation. Missing,
ambiguous, tampered, or stale evidence is rejected.

## Rationale

The local controller is the only provider executed by the local Station Agent;
the remote identity is an authorized physical target, not a provider silently
loaded into the wrong Agent. Exact bindings remove capability ambiguity, while
remote leases prevent two controllers from concurrently commanding the same
equipment.

## Consequences

### Positive

- Cross-Station privilege is action-specific and reviewable.
- Multiple child Systems may expose the same Capability without ambiguous
  routing.
- Signed packages and release hashes retain the full authorization evidence.
- Stale remote leases reject execution before provider invocation.

### Negative

- Line designers must add a fixed local controller resource and an explicit
  authorization for each remote Flow action.
- A controller adapter must understand the canonical remote-target envelope.

### Risks And Mitigations

- Risk: a grant references a Flow action that changes after editing.
  Mitigation: save and publication require one exact action-id and tuple match.
- Risk: metadata is changed after publication.
  Mitigation: release content hashes and Station package signatures cover the
  authorization and authorized-action link.
- Risk: a remote lease is replaced while a command is queued.
  Mitigation: Runtime and Station Runtime validate current fencing evidence
  immediately before provider invocation.

## Related Decisions

- ADR-0002: Enforce DDD Layering And Boundaries.
- ADR-0009: Model Production Lines Separately And Compile Blockly Directly.
- ADR-0010: Make System Canonical And Layout Hierarchical.
- ADR-0011: Coordinate Production Through Station Agents.
