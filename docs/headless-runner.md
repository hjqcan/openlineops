# Headless Runner

The `runner` release artifact starts a completed automation project without
opening Studio. It is a self-contained `win-x64` bundle and does not require a
separately installed .NET runtime.

Verify the outer release manifest and checksum set before extraction, then
verify the exact payload described by `bundle-manifest.json` and
`bundle-checksums.sha256`. Production releases require a valid Authenticode
signature on `OpenLineOps.Runner.exe`.

Run one immutable published Project Snapshot with:

```powershell
.\OpenLineOps.Runner.exe run C:\Projects\LineA `
  --snapshot active `
  --production-unit-id 8a9d9629-598e-4e96-a8e7-5df8d7da44a9 `
  --identity UNIT-001 `
  --actor operator-a
```

The target may be a Project directory or its `.oloproj` file. `--snapshot`
defaults to `active`; it may also name an immutable snapshot. The production
unit ID, external identity, and actor are required. `--run-id` may supply the
non-empty canonical GUID used for an idempotent caller-owned run request.

The process writes machine-readable JSON status and returns stable process exit
codes. Runner starts the same Coordinator, Station-job Outbox, result Inbox,
and transport hosted services as the API host, submits the run asynchronously,
and waits on its durable terminal state; it does not execute the route through
a private synchronous path. Cancellation through Ctrl+C requests a controlled
stop. Use a service, scheduler, MES adapter, or operator launcher to invoke the
executable under an identity that has only the Project, runtime state, device,
and artifact access required by that automation line.

## Formal Station execution

A production deployment uses `StationExecution:Provider=Agent`, PostgreSQL for
the production coordination store, and RabbitMQ for Station jobs and results.
Each Station mapping binds the frozen Project, Application, and Station System
to one Agent and Station identity. The Agent receives only a signed,
content-addressed `.olopkg`; its deployment catalog, package manifest,
signature, and content hash must all agree before execution. `InProcess` and a
disabled transport are development-only configurations and are not accepted by
the formal process gate.

The Runner process owns the central coordination lifetime for that invocation.
The Windows Agent is a separately staged, long-running process and may be
installed as a service on a Station computer. Neither process needs Studio or
the authoring API after the Project Snapshot and Station package have been
published.

## Release process gate

The release gate proves the complete no-IDE boundary against a real isolated
PostgreSQL schema and real RabbitMQ broker:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File eng/verify-runner-staged-agent-e2e.ps1 `
  -ArtifactsRoot artifacts/release `
  -PostgreSqlConnectionString $env:OPENLINEOPS_POSTGRES_CONNECTION_STRING `
  -RabbitMqUri $env:OPENLINEOPS_RABBITMQ_URI `
  -Configuration Release `
  -NoBuild -NoRestore
```

The gate extracts the exact Runner and Agent archives recorded by
`release-manifest.json`, binds both executable hashes to their inner bundle
manifests and checksums, authors and freezes a Simulator Project, then starts
the real staged `OpenLineOps.Agent.exe` and `OpenLineOps.Runner.exe`. The
authoring host creates the immutable input only; it is not present in the
execution path.

Passing evidence requires one terminal Production Run, one Station job and one
result in PostgreSQL; one Agent SQLite inbox item and one acknowledged terminal
checkpoint; drained RabbitMQ queues; one immutable Project Trace; distinct
Runner and Agent PIDs; and complete bounded cleanup. The fixture intentionally
has no artifacts and configures a closed loopback artifact endpoint, so any
unexpected upload attempt fails the run. The public evidence directory contains
only sanitized `evidence.json` and one TRX proving exactly one Passed and zero
Skipped tests. Validate it independently with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File eng/verify-runner-staged-agent-evidence.ps1 `
  -EvidenceRoot output/runner-staged-agent-e2e `
  -RequirePassed
```

This is deliberately the basic command-transport closure: Simulator Flow,
Runner, PostgreSQL, RabbitMQ, Agent, and Trace, with zero artifacts. It does not
claim vendor-program or artifact-transfer coverage. Its safety queues must stay
empty; the configured `where.exe` path is only a valid, independent no-op value
and this gate does not claim Emergency Stop or Safe Stop actuator coverage. The
staged Agent safety gate owns that claim. The packaged Studio combined gate owns
the real vendor helper, artifact upload/download/hash, and operator workflow
evidence so the gates have explicit, non-overlapping claims.

The test accepts seven dedicated `OPENLINEOPS_RUNNER_AGENT_GATE_*` variables,
including an explicit `ENABLED=1` and wrapper-owned scope ID. When all seven are
absent, the ordinary test suite may omit the external gate. If any one is
present, every bundle, PostgreSQL, RabbitMQ, evidence, enable, and scope input is
mandatory; an incomplete gate configuration fails closed. The wrapper owns the
scope names, so it can independently remove the PostgreSQL schema, RabbitMQ
queues, extracted bundles, generated signing key, and test workspace after a
timeout or hard process-tree kill.
