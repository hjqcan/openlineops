# OpenLineOps Production-Line Implementation Baseline

Last updated: 2026-07-23

## Product Contract

OpenLineOps is an IDE and headless runtime platform for third parties to build,
publish, operate, and trace automation production lines. Studio opens a root
`.oloproj`; each independently portable Application owns one `.oloapp` and all
of its topology, layout, production route, Flows, configuration, plugins, and
external-program resources.

The authoring model is project-first and Blockly-first. Python Script remains a
separate advanced Flow node. Runtime never treats Python generated from Blockly
as a second source of truth.

## Formal Boundaries

### Production

Production owns the immutable process definition:

- `ProductModelDefinition`;
- Station-System-bound `OperationDefinition` nodes;
- explicit Station, Fixture, Device, Slot Group, and Slot resources;
- sequential, conditional, bounded-rework, parallel-fork, parallel-join, and
  terminal `RouteTransition` edges;
- exact Action authorization compiled from canonical Flow IR.

A Station is a System. There is no duplicate workstation aggregate and no
ordered test-stage root. An external vendor program is an Application resource
referenced by an ordinary Flow Action.

### Runtime

Runtime owns execution and WIP:

- `ProductionUnit`, `ProductionLot`, `Carrier`, genealogy, material location,
  Slot occupancy, and product disposition;
- `ProductionRun`, `OperationRun`, typed Production Context output, route
  decisions, Incidents, and recovery decisions;
- Station, Fixture, Device, Slot Group, and Slot leases with monotonically
  increasing fencing tokens;
- persisted, rebuildable line, Station, Slot, queue, and active-run projections.

`ExecutionStatus`, `ResultJudgement`, and product disposition are independent.
A vendor-declared nonconforming result is a completed execution with a failed
judgement. Infrastructure or protocol failure has an unknown judgement and an
Incident.

### Coordinator And Agent

The Coordinator is authoritative for route, material, leases, commands, and
projections. PostgreSQL persists production coordination and a transaction
Outbox. RabbitMQ carries Station jobs and safety commands.

Each Station has one Windows Agent identity. The Agent persists its command
Inbox, result Outbox, checkpoints, cancellation decisions, and latest resource
fences in file-backed SQLite. Global idempotency keys prevent redelivery from
replaying hardware. An interrupted non-idempotent action becomes
`RecoveryRequired` and requires an explicit reconcile, retry, abort, or scrap
decision.

Normal work, cancellation, Safe Stop, and Emergency Stop are distinct message
contracts. Emergency Stop uses the independent high-priority Station safety
channel and does not wait behind the normal job queue.

### Immutable Deployment

Publication freezes one strict release shape and creates a signed `.olopkg` for
each Station identity. A package includes the frozen Application content and
all referenced Flow, configuration, plugin, and external-program files. The
Agent verifies the trusted RSA-PSS signature, exact archive membership, every
file hash, Station identity, and package content digest before installing it in
a read-only content-addressed cache.

Station Runtime accepts one strict request/result document protocol. External
program execution uses an isolated working directory, a restricted account and
AppContainer profile, a Windows Job Object with kill-on-close, explicit network
capabilities, bounded CPU/memory/process/output/artifact resources, and complete
process-tree termination. Stdout, stderr, reports, images, CSV, and PDF evidence
are hashed artifacts uploaded to central storage before the public completion
event is acknowledged.

### Trace

Trace preserves immutable evidence for:

- Project, Application, release, line, Production Unit, lot, and genealogy;
- every material location transition and Slot occupancy transition;
- Operation attempts, typed outputs, route decisions, commands, execution
  status, judgement, disposition, Incidents, resource fences, and artifacts;
- local and remote Agent execution through the same strict evidence contract.

Trace read models are projections. They can be rebuilt from persisted Runtime,
material, Station-job, and evidence records.
Each immutable Production Run Trace is frozen through the Run's terminal
`CompletedAtUtc`. The Product Material Lifecycle read model is rebuilt through
the latest persisted material event, so final unload, handoff, and later
disposition evidence remain visible without mutating the Run Trace. Operator
clients read it with
`GET /api/traceability/production-units/{productionUnitId}/material-lifecycle`;
`observedThroughUtc` identifies the latest state or evidence included. Carrier
location and Slot evidence is included only in the half-open Carrier membership
interval `[enteredAtUtc, leftAtUtc)`. While that interval remains open,
`currentCarrierLocation` reports the Carrier's latest physical location.
Production Unit material events bind to the active Production Run; after that
Run becomes terminal, final unload, handoff, arrival, and disposition events
bind to `LastProductionRunId` until another Run becomes active. A Unit that has
never entered a Run keeps independent manual material events unbound.
The public Coordinator contract is read/export only for Trace records. Raw Trace
documents cannot be posted or imported; the sole write path projects persisted
terminal Production Run evidence.

## Studio And Headless Operation

Before a project is open, Studio presents New, Open, and Recent project entry
actions. After opening, it presents project/application Explorer, hierarchical
2D layout editing, production route graph editing, Blockly/Python Flow editing,
external-program import and protocol trial, configuration, run monitoring, and
Trace.

Edit mode changes Application source. Run mode consumes only an immutable
published snapshot. The shared 2D/3D projection exposes active products,
Station queues, Slot occupancy, resource states, current Operation, and material
flow. Operator commands include Pause, Continue, Stop, Hold, Release, Rework,
Scrap, Safe Stop, recovery decisions, and Emergency Stop.

`OpenLineOps.Runner` starts an immutable published project without opening the
IDE. Production Station computers run `OpenLineOps.Agent` and
`OpenLineOps.StationRuntime` without Studio.

## Public HTTP Surface

- `POST /api/production-units`
- `POST /api/production-units/{id}/arrivals`
- `GET /api/automation-projects/{projectId}/snapshots/{snapshotId}/production-run-context`
- `POST /api/production-runs` with immutable Project Snapshot and Production Unit identities
- `GET /api/production-runs/{id}`
- `POST /api/production-runs/{id}/commands/{command}`
- `GET /api/operations/lines/{lineId}/state`
- `GET /api/operations/active-runs`
- `GET /api/traceability/production-units/{id}/material-lifecycle`

The raw production-run injection endpoint is intentionally absent. A run can be
created only from an immutable published Project Snapshot.

## Required Verification

The repository gate is the combination of:

```powershell
dotnet build OpenLineOps.sln --configuration Release --property:TreatWarningsAsErrors=true
dotnet test OpenLineOps.sln --configuration Release --no-build -m:1
$env:OPENLINEOPS_RUN_POSTGRES_INTEGRATION = "1"
$env:OPENLINEOPS_RUN_RABBITMQ_INTEGRATION = "1"
dotnet test tests/OpenLineOps.PostgresIntegration.Tests/OpenLineOps.PostgresIntegration.Tests.csproj --configuration Release --property:TreatWarningsAsErrors=true
powershell -NoProfile -File eng/verify-no-version-suffix-implementations.ps1
powershell -NoProfile -File eng/verify-no-legacy-production-contracts.ps1
powershell -NoProfile -File eng/verify-no-technical-debt-markers.ps1
powershell -NoProfile -File eng/verify-solution-project-coverage.ps1
powershell -NoProfile -File eng/verify-ci-workflow-actions.ps1
powershell -NoProfile -File eng/verify-ci-workflow-actions.tests.ps1
powershell -NoProfile -File eng/verify-evidence-validation.tests.ps1
powershell -NoProfile -File eng/verify-studio-two-agent-production-evidence.tests.ps1
powershell -NoProfile -File eng/verify-runner-staged-agent-evidence.tests.ps1
powershell -NoProfile -File eng/verify-open-source-metadata.ps1
powershell -NoProfile -File eng/verify-third-party-license-metadata.ps1
powershell -NoProfile -File eng/verify-dotnet-package-vulnerabilities.ps1
powershell -NoProfile -File eng/verify-dotnet-package-vulnerabilities.tests.ps1
powershell -NoProfile -File eng/verify-release-staging-security.ps1
powershell -NoProfile -File eng/verify-station-agent-content-cache-contract.ps1
$env:OPENLINEOPS_RABBITMQ_URI = "amqp://guest:guest@127.0.0.1:5672/%2f"
$env:OPENLINEOPS_POSTGRES_CONNECTION_STRING = "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=<ephemeral-password>"
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-staged-agent-bundle-e2e.ps1 -Configuration Release -NoBuild -NoRestore
$externalAbortScope = [System.Guid]::NewGuid().ToString("N")
$cleanupRoot = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) "openlineops-agent-service-cleanup"))
New-Item -ItemType Directory -Path $cleanupRoot -Force | Out-Null
$agentRoot = [System.IO.Path]::GetFullPath((Resolve-Path "artifacts/release-work/agent").Path)
$samplePluginRoot = [System.IO.Path]::GetFullPath((Resolve-Path "artifacts/release-work/sample-plugin").Path)
$apiRoot = [System.IO.Path]::GetFullPath((Resolve-Path "artifacts/release-work/api").Path)
$cleanupManifest = Join-Path $cleanupRoot "rabbitmq-$externalAbortScope.json"
$readyPath = Join-Path $cleanupRoot "external-abort-ready-$externalAbortScope.json"
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-agent-service-external-abort-cleanup.ps1 -AgentBundleRoot $agentRoot -SamplePluginRoot $samplePluginRoot -ApiBundleRoot $apiRoot -Scope $externalAbortScope -ManifestPath $cleanupManifest -ReadyPath $readyPath -Configuration Release -NoBuild -NoRestore
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-studio-two-agent-production-closure.ps1 -Configuration Release -NoBuild -NoRestore
npm --prefix apps/desktop run test:production-command-policy
npm --prefix apps/desktop run test:process-problem-location
npm --prefix apps/desktop run test:draft-transition-guard
npm --prefix apps/desktop run test:topology-draft-workspace
npm --prefix apps/desktop run test:configuration-draft-workspace
npm --prefix apps/desktop run test:external-program-directory-import
npm --prefix apps/desktop run test:runtime-data-binding
npm --prefix apps/desktop run test:production-route-runtime
npm --prefix apps/desktop run smoke:e2e:packaged-existing
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-production-closure-evidence.ps1 -EvidenceRoot artifacts/production-closure-e2e -RequirePassed
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-studio-two-agent-production-evidence.ps1 -EvidenceRoot output/studio-two-agent-production-closure
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-runner-staged-agent-e2e.ps1 -Configuration Release -NoBuild -NoRestore
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-runner-staged-agent-evidence.ps1 -EvidenceRoot output/runner-staged-agent-e2e -RequirePassed
git diff --check
```

Desktop verification additionally runs strict TypeScript checking, production
bundling, the cold-start production-line projection test, the trusted
Application-extension ZIP import boundary, route graph validation and persisted
layout conflict/reopen behavior, the shared Operations/2D/3D runtime route
projection (current transition, traversed trail, flow direction, and terminal
disposition), Trace artifact save confinement, Electron
smoke, npm vulnerability audit, and a packaged Windows executable E2E. The
packaged E2E covers concurrent Stations, vendor pass, product nonconformance
with rework, cancellation with full process-tree termination, and
crash/recovery without hardware replay. It also starts from incompatible local
runtime state, binds that state to the packaged runtime content, proves a
same-package restart preserves current Trace and Artifact bytes, and proves a
second Studio instance cannot start another backend or race the destructive
binding for the same user-data directory. The packaged API is bound to the
exact Electron parent process and exits after an abrupt parent kill, while an
explicit stop must confirm the process tree has exited before another backend
can start.
It also proves that immutable Run Trace bytes do not change after a terminal
Slot unload while the latest Product Material Lifecycle API and Studio panel
show the resulting Station queue location and `Available` Slot transition.

The staged Agent, packaged-to-two-Agent, and Runner commands are not optional:
they fail when their real PostgreSQL/RabbitMQ inputs are absent or unreachable.
Their evidence must pass
`eng/verify-staged-agent-evidence.ps1`, including authenticated central Artifact
upload and Operator GET/hash verification, offline durable completion,
once-only redelivery, the fixed `NT AUTHORITY\LocalService` account and SID, a
canonical unique Windows service name and derived service SID, an enabled
service-logon SID, an enabled-and-restricted exact service SID, no UAC linked
token (`TokenElevationTypeDefault`), complete absence of the Administrators
group, SCM
start/stop/restart/delete lifecycle, the raw evidence hash, and an external
`dotnet test` driver-tree abort cleanup proof, including testhost descendants,
under a separate run scope. Strict private cleanup manifests bind only role,
service suffix/name, fixed LocalService identity, derived service SID and
`Restricted` type, copied Agent path/hash, the exact Windows Temp owned root,
and the exact role-specific `CommonApplicationData` (`%ProgramData%`)
package-cache root beneath its deterministic anchor;
wrapper `finally` blocks and independent workflow `always()` steps invoke the
same bounded, idempotent scavenger. Studio's two Station services share the
LocalService base account but must expose distinct restricted service SIDs.
Because Windows checks `TOKEN_DUPLICATE` against the service token object's own
DACL, the external test runner never opens, copies, or changes a Station token.
Windows E2E instead starts a random, one-shot helper under the unique virtual
account `NT SERVICE\<random-service-name>`, not the shared LocalService account.
The helper proves that exact SID as `TokenUser`; it does not depend on Windows
also enumerating the same virtual-account SID as a duplicate `TokenGroups`
entry. The SERVICE well-known SID `S-1-5-6` remains enabled and the helper SID
must be absent from `TokenRestrictedSids`.
Before any Station capability exists, a minimal-rights coordination pipe binds
that exact account SID to the exact SCM helper PID and protected helper hash. A
scoped process-object lease then grants only that SID
`PROCESS_CREATE_PROCESS | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE` on
the exact retained Station PID. After validating the Station SCM binding,
creation time, image and hash, the helper uses
`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` to create a fixed, suspended relay;
Windows supplies the relay with the Station process token. A simultaneous
`PROC_THREAD_ATTRIBUTE_JOB_LIST` places it atomically in a non-inheritable,
kill-on-close job with an active-process limit of one before its only thread can
run. A Station already contained by another job is rejected because its inherited
job policy cannot be proven compatible. No token handle crosses a process
boundary and no token DACL is read or modified.

Immediately after native process creation, and before any later relay query or
validation can fail, the helper reports only the observed relay PID through the
authenticated coordination channel. The runner immediately retains that exact
suspended-process handle, reads the authoritative creation time, verifies its
image and hash, and returns the creation time in its capture acknowledgement.
The helper binds that value to an independent creation-time read from its
original native process handle before validating the image, containment job and
source process, closes its only
`PROCESS_CREATE_PROCESS` handle, and sends a separate ready marker. Before
acknowledging resume, the runner rehashes the exact full self-contained helper
inventory, revalidates all bridge ACLs, restores and bytewise revalidates the
original Station process DACL, and creates the control pipe. The creation lease
and strong handle therefore cannot span `RunAsClient` or any business-side
effect.
The runner treats its PID-opened handle as provisional until that ready marker,
or a bounded helper result carrying the independently bound creation time,
confirms the same process object. A pre-confirmation failure closes the
provisional handle without terminating through it; termination of the real
relay is then guaranteed by closure of the helper-owned, non-inheritable
kill-on-close job.

The relay revalidates its restricted LocalService identity, enabled-and-
restricted Station SID, its own protected image/hash and the frozen Agent
image/hash, then connects to the single-instance reverse pipe, whose Station SID
ACE contains only read/write/synchronize client rights. The runner binds the
connection with `GetNamedPipeClientProcessId` and requires it to match the relay
PID and creation time retained by the runner and independently confirmed by the
authenticated helper; only then
does it execute the boundary assertion through `RunAsClient`. The protected
bridge root physically separates immutable `helper/` and `protocol/` trees from
the writable `result/` tree. The helper and Station SIDs have only
read/execute/synchronize rights over every executable and protocol dependency;
only the helper SID has bounded modify rights inside `result/`. An exact
path/length/SHA-256 inventory rejects missing, changed or additional helper
files before service start, after helper capture, before relay resume and after
completion. A gate passes only after the exact
relay and helper PIDs have exited, the one-process job is closed, the original
source-process DACL is restored and revalidated, and all service and temporary
state is deleted. Failure to prove termination or exact DACL restoration is a
hard failure. The source-process lease never grants shared LocalService,
service-logon, Administrators, or runner access and never enables a debug or
ownership privilege.
This helper is self-contained, accepts only its fixed nonce-bound request, is
absent from every deployable artifact, and does not relax any production Agent
pipe, cache, or token ACL.
`eng/verify-studio-two-agent-production-evidence.ps1` and
`eng/verify-runner-staged-agent-evidence.ps1` bind that two-Agent proof and the
Runner-to-SCM-Agent proof to their public roots. The production-closure scanner
accepts only its exact
public manifest: summary, screenshots, verified Trace saves, frozen manifest,
public signing key, two `.olopkg` files, and two deployment catalogs. Studio
user data, project working files, tokens, private keys, browser profiles, and
raw process logs remain outside the public artifact root under the system
temporary directory and are removed after the run.

The implementation contains one current schema per owned resource, no format
fallbacks, no legacy execution entry point, and no implementation-generation
suffixes.
