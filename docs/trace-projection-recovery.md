# Trace Projection Recovery

Production Trace records are projections of the immutable terminal evidence stored with each
Production Run. The Coordinator API can rebuild a lost or newly provisioned Trace database during
host startup. Rebuild is deliberately disabled by default and has no public HTTP endpoint.

Run Trace evidence remains frozen through the Run's terminal `CompletedAtUtc`. Product-level
material history is queried separately through
`GET /api/traceability/production-units/{productionUnitId}/material-lifecycle`; that read model is
rebuilt from the current Runtime material aggregate and append-only timeline through the latest
event. It therefore includes terminal unload and later transfer evidence without rewriting the Run
Trace. When a Production Unit occupies a Carrier position, Carrier location and Slot evidence is
included only for the half-open membership interval `[enteredAtUtc, leftAtUtc)`.

Enable it for one controlled startup with configuration:

```text
OpenLineOps__Traceability__ProjectionRebuild__Enabled=true
OpenLineOps__Traceability__ProjectionRebuild__PageSize=100
```

`PageSize` must be between 1 and 1000. The startup service pages through the frozen terminal
evidence manifest and idempotently recreates Trace records before the terminal outbox worker starts.
Any invalid configuration, non-advancing page cursor, persistence failure, or projection conflict
fails host startup; the API must not be considered ready. After a successful recovery startup,
disable the setting again and retain the rebuilt Trace database as normal durable state.
