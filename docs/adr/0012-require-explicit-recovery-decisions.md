# ADR-0012: Require Explicit Recovery Decisions

## Status

Accepted

Refines the recovery policy in ADR-0011.

## Date

2026-07-11

## Context

A station can lose contact after a non-idempotent hardware action has started.
At that point neither the Coordinator nor the Agent can infer whether the
physical action happened. Automatically replaying the action can damage a
product, fixture, device, or operator. Treating reconciliation as ordinary
cancellation loses the observed product result and prevents the route from
continuing from verified physical evidence.

Recovery choices must survive retries, service restarts, Trace generation, and
operator handoff. They must also remain distinct from normal Pause, Stop,
Cancel, Hold, Rework, and Safe Stop controls.

## Decision Drivers

- Never replay uncertain hardware work without an explicit operator choice.
- Preserve the difference between observed product quality and execution
  infrastructure failure.
- Let a verified physical result continue conditional routing without invoking
  the provider again.
- Make repeated HTTP or broker delivery idempotent without silently accepting
  different evidence under the same identity.
- Retain enough evidence to audit who resolved an interruption and why.

## Considered Options

### Automatically retry the interrupted Operation

- Pros: minimal operator involvement.
- Cons: unsafe for motion, dispensing, programming, destructive testing, and
  other non-idempotent actions.

### Map reconciliation to Cancel

- Pros: simple terminal behavior.
- Cons: discards observed judgement and typed outputs, cannot follow the
  intended route, and falsely represents a verified completion as aborted.

### Persist an explicit Recovery Decision

- Pros: safe non-replay semantics, durable evidence, exact idempotency, and
  honest routing and Trace state.
- Cons: requires an operator workflow and strict evidence validation.

## Decision

Every run in `RecoveryRequired` is resolved only by an immutable
`ProductionRecoveryDecision` of exactly one kind:

- `Reconcile` identifies the interrupted Operation Run and records an observed
  `Passed`, `Failed`, or `NotApplicable` judgement plus typed outputs. Runtime
  completes that attempt from evidence, releases only that attempt's lease,
  applies the normal route, and never invokes the Station provider.
- `Retry` identifies the open Operation definition, cancels interrupted open
  attempts, creates a new attempt, and permits dispatch only because the
  operator explicitly requested it.
- `Abort` ends the run as `Canceled + Aborted` and holds the product.
- `Scrap` ends the run as `Completed + Failed` with `Scrapped` disposition.

A decision contains a global decision id, operator identity, canonical reason,
evidence reference, UTC decision time, and the fields required by its kind.
The decision id is an idempotency identity. Repeating exactly the same decision
is a no-op and does not advance persistence revision. Reusing the id with any
different immutable field is a conflict.

Normal `Cancel` is rejected while a run is `RecoveryRequired`. Safe Stop and
Emergency Stop remain safety controls and do not substitute for the subsequent
recovery disposition.

Runtime snapshots and persistence contain the Recovery Decisions. A
reconciled Operation is exposed to Trace as `Reconciled`, not as a normally
completed Runtime Session. Trace stores a deterministic audit entry with the
operator, evidence reference, reason, observed judgement, and typed outputs.

## Rationale

The physical world is the authority after an uncertain non-idempotent action.
An explicit decision lets an operator reconcile that authority with the
digital route without inventing execution evidence. Separate decision kinds
make retries deliberate, terminal dispositions honest, and audits
reconstructable.

## Consequences

### Positive

- No recovery path automatically repeats a hardware action.
- Verified results can continue judgement or typed-output routing.
- API retries cannot duplicate recovery transitions.
- Trace distinguishes normal execution from operator reconciliation.

### Negative

- Operators need evidence collection and authorization procedures.
- Parallel interrupted Operations may require more than one Reconcile decision.
- Recovery screens must collect typed values and exact Operation identities.

### Risks And Mitigations

- Risk: an operator records evidence for the wrong attempt.
  Mitigation: Reconcile requires the exact `OperationRunId` and restore-time
  validation ties every decision to matching terminal Operation evidence.
- Risk: a stale client reuses a decision id with changed content.
  Mitigation: immutable-evidence comparison rejects the request as an identity
  conflict.
- Risk: reconciliation releases unrelated parallel resources.
  Mitigation: Reconcile releases only the selected Operation Run lease; the run
  stays in recovery while another interrupted Operation remains running.

## Related Decisions

- ADR-0002: Enforce DDD Layering And Boundaries.
- ADR-0009: Model Production Lines Separately And Compile Blockly Directly.
- ADR-0011: Coordinate Production Through Station Agents.
