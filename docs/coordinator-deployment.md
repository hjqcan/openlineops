# Coordinator Production Deployment

The Coordinator is the authoritative production-line control plane. It owns
WIP, route decisions, resource leases, Station command outboxes, Agent result
inboxes, monitoring projections, and Trace metadata. A production deployment
must not use the local SQLite/InMemory/InProcess composition: startup accepts
Agent execution only when Runtime coordination is PostgreSQL and Station
transport is RabbitMQ.

## Required production shape

```text
Operator / MES / PLC bridge
          |
       HTTPS API
          |
  OpenLineOps Coordinator
      |            |
 PostgreSQL     RabbitMQ
      |            |
 Trace store    Station Agents
```

Use one Coordinator identity per deployment and one Windows Agent identity per
physical Station. PostgreSQL and RabbitMQ are authoritative services; do not
place them on a Station workstation or inside the Studio process. Run at least
two Coordinator instances behind the HTTPS endpoint only after all instances
share the same PostgreSQL database, deployment catalog, package distribution,
Trace artifact store, and caller credential configuration.

The checked-in `appsettings.json` contains only the strict shape and empty
fail-fast placeholders. Supply secrets through the deployment secret store or
process environment. Never commit a raw API token, token hash paired with its
raw token, PostgreSQL password, RabbitMQ password, or package-signing private
key.

## PostgreSQL

Set these exact values:

```powershell
$env:OpenLineOps__Runtime__Coordination__Provider = 'PostgreSql'
$env:OpenLineOps__Runtime__Coordination__ConnectionString = `
  'Host=db.internal;Port=5432;Database=openlineops_runtime;Username=openlineops_runtime;Password=<secret>;SSL Mode=VerifyFull;Root Certificate=C:\OpenLineOps\trust\postgres-ca.pem'
$env:OpenLineOps__Runtime__StationExecution__Provider = 'Agent'
```

Use a dedicated database principal. Grant schema migration rights to a
deployment identity, then reduce the runtime identity to the tables and
sequences owned by Runtime coordination. Back up WIP, command/result inboxes and
outboxes, leases, presence, and recovery evidence together. A restore that
omits any of those tables is not a valid production recovery.

Coordinator startup applies the strict coordination schema before accepting
work. `/health/ready` opens this PostgreSQL connection and executes a probe. Do
not route traffic to an instance until readiness succeeds.

## RabbitMQ Station boundary

Use `amqps`, a private virtual host, broker certificates trusted by both the
Coordinator and Windows Agents, and a unique non-default principal for every
Agent. Delete or disable `guest` for the production virtual host. Never share
an Agent credential between Stations. With `RequireTls=true`, Coordinator
startup fails closed unless `BrokerUri` includes a non-`guest` username and a
non-empty password; credentialless and `guest` URIs are accepted only by the
explicit `RequireTls=false` local/integration-test mode.

```powershell
$env:OpenLineOps__Runtime__AgentTransport__Provider = 'RabbitMq'
$env:OpenLineOps__Runtime__AgentTransport__BrokerUri = `
  'amqps://coordinator:<secret>@rabbit.internal:5671/openlineops'
$env:OpenLineOps__Runtime__AgentTransport__RequireTls = 'true'
$env:OpenLineOps__Runtime__AgentTransport__CoordinatorId = 'coordinator.line-a'
```

Broker permissions are part of the security boundary, not an optional
hardening step:

- the Coordinator principal may publish only to Station job and safety command
  exchanges and consume only its own result and safety-acknowledgement queues;
- an Agent principal may consume only the job/safety queues for its exact
  `AgentId + StationId`, and may publish only to the Station event and safety
  event exchanges;
- configure permissions are limited to the exact exchanges and queues that the
  process declares;
- RabbitMQ topic permissions restrict an Agent's event routing key to
  `station.<sha256(StationId)>.*`. This prevents one Station credential from
  forging another Station's completion, heartbeat, or safety acknowledgement;
- no application principal receives administrator, management, policy, vhost,
  user, or wildcard permissions.

Record the broker permission export with commissioning evidence. After every
broker policy change, prove that the intended Agent can complete one signed
job and that a different Agent principal receives `ACCESS_REFUSED` when it
publishes or consumes through that Station's routes.

The staged Windows release gate performs a real broker outage during a signed
vendor program: the Agent must finish into its durable SQLite outbox, deliver
the result once after RabbitMQ returns, and reject redelivery after restart.
This gate is mandatory; a continuously available broker test is insufficient.

## Station deployment catalog and signed packages

Set the package and routing roots to shared, protected locations visible to all
Coordinator instances:

```powershell
$env:OpenLineOps__Projects__StationPackages__DistributionDirectory = `
  '\\files.internal\OpenLineOps\packages'
$env:OpenLineOps__Projects__StationPackages__DeploymentCatalogDirectory = `
  '\\files.internal\OpenLineOps\deployment-catalog'
$env:OpenLineOps__Projects__StationPackages__SigningKeyId = 'line-release-2026'
$env:OpenLineOps__Projects__StationPackages__SigningPrivateKeyPath = `
  'C:\OpenLineOps\secrets\line-release-private.pem'
$env:OpenLineOps__Runtime__AgentTransport__DeploymentCatalogDirectory = `
  '\\files.internal\OpenLineOps\deployment-catalog'
```

For each Station configure a stable mapping with `ProjectId`, `ApplicationId`,
`StationSystemId`, `AgentId`, and physical `StationId`. The Snapshot and package
hash come from the signed deployment catalog; do not put mutable hashes into the
stable mapping. The private signing key stays on the Coordinator deployment
identity. Agents receive only the matching public key.

The runtime mapping is explicit configuration; it is not inferred from a
deployment catalog. The catalog proves the frozen Snapshot, Production Line,
Station System, and package hash, while the mapping assigns that logical
Station System to one physical Agent and Station identity. A two-Station
deployment uses these exact keys:

```powershell
$env:OpenLineOps__Runtime__AgentTransport__Deployments__0__ProjectId = 'project.line-a'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__0__ApplicationId = 'application.line-a'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__0__StationSystemId = 'station.assembly'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__0__AgentId = 'agent.assembly'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__0__StationId = 'physical.assembly'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__1__ProjectId = 'project.line-a'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__1__ApplicationId = 'application.line-a'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__1__StationSystemId = 'station.test'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__1__AgentId = 'agent.test'
$env:OpenLineOps__Runtime__AgentTransport__Deployments__1__StationId = 'physical.test'
```

## Startup Automation Projects

The Studio-authored `.oloproj`, its Application directories, and its immutable
release directory are runtime inputs. Configure every Project that this
Coordinator serves so a restart restores the published Project identity before
Runtime recovery, the RabbitMQ result inbox, or Production Run coordination
starts:

```powershell
$env:OpenLineOps__Projects__StartupWorkspaces__ProjectFiles__0 = `
  'C:\OpenLineOps\projects\line-a\line-a.oloproj'
```

`ProjectFiles` is a zero-based contiguous array. Every value must be a unique,
canonical absolute path with the exact `.oloproj` extension. If the section is
present but empty, contains an unknown key, has a gap, repeats a path or Project
identity, or any Project cannot be fully validated and opened, Coordinator
startup fails closed. This prevents a restarted headless API from accepting a
Station result or material arrival before its signed Project context exists.

An Engineering caller may still load a Project deliberately with
`POST /api/automation-project-workspaces/open`. Production supervision should
prefer `StartupWorkspaces` so restart does not depend on a human or an ordering
race with Agent events.

## Production HTTP admission sequence

After `/health/ready` succeeds, the normal public path is:

1. `POST /api/production-units` registers the product identity.
2. `POST /api/production-units/{productionUnitId}/arrivals` records its exact
   entry Station, signed package hash, Project Snapshot, and Production Line.
3. `POST /api/production-runs` supplies only `projectId`,
   `projectSnapshotId`, `productionRunId`, and `productionUnitId`; a successful
   admission returns `202 Accepted` and a Location header immediately.
4. The Coordinator derives the graph and execution plans from the immutable
   Project release, acquires persisted resource leases, commits a Station Job
   outbox, publishes it through RabbitMQ, and advances the route from the
   authenticated Agent completion.
5. Before a cross-Station Operation can run, PLC/manual Agent ingress or an
   Operator call to the same arrivals endpoint records the product's physical
   arrival at the next Station. A completion never fabricates material motion.

For two concurrent products, repeat steps 1-3 with distinct Unit and Run UUIDs.
Station, Slot, Fixture, and Device leases—not a Project-wide lock—determine
which Operations overlap. Use `GET /api/production-runs/{id}` and
`GET /api/operations/lines/{lineId}/state` to observe the durable result.

## Central Trace artifacts

Trace metadata and bytes must resolve through one storage contract. Configure
the Coordinator Trace artifact root on durable storage accessible only to the
Coordinator service identity. Every Agent streams each bounded artifact to the
authenticated HTTPS Trace API with its exact Agent, Station, Job, artifact kind,
byte count, and SHA-256 headers. The API authorizes the Job against the durable
coordination store, persists immutable bytes and receipt, and returns the
content-addressed receipt before the Agent may publish completion. No Agent or
operator receives filesystem access to the Trace root.

Provision one strong `StationAgent` bearer credential per Agent. Its Actor ID
must equal `AgentId` and its Station claim must equal `StationId`; do not reuse
Engineering, Operator, Safety, broker, or another Station credential. Agents
use HTTPS except for explicit loopback commissioning. Operators retrieve
evidence through the authenticated Trace GET API, which verifies the storage
key, byte count, and SHA-256 recorded in Trace.

Back up Trace metadata and artifact bytes as one recovery unit. Commissioning
must download stdout, stderr, CSV, PDF, and image artifacts from the Trace API
and compare every size and SHA-256 to its immutable Trace record.

## API authentication and HTTPS

All API routes except minimal liveness/readiness require the
`OpenLineOpsBearer` scheme. Provision separate caller credentials and assign
only the exact `Engineering`, `Operator`, and `Safety` roles required. Emergency
Stop uses a Safety-only credential that is never exposed to the Electron
renderer or reused for normal operation.

Generate 32 random bytes, encode them as unpadded base64url, store the raw token
in the caller's secret store, and give the Coordinator only its lowercase
SHA-256:

```powershell
$bytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
$hash = [Convert]::ToHexString(
  [Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($token))).ToLowerInvariant()
```

Bind `OpenLineOps:Security:Callers` entries with a unique `CredentialId`, stable
`ActorId`, `TokenSha256`, and explicit `Roles`. The API rejects an empty caller
list, malformed or duplicate hashes, unsupported roles, and actor spoofing in
request bodies. Rotate credentials by provisioning a new unique entry, updating
the caller, verifying it, then removing the old entry and restarting every
Coordinator instance.

Remote cleartext HTTP is rejected. Terminate TLS in the API process or at a
trusted reverse proxy that preserves the real remote address and forwards only
from the private ingress. Loopback HTTP is reserved for the packaged local
Studio backend.

## Process supervision

Run the API under the organization's service manager (Windows service wrapper,
systemd, or container orchestrator). The supervisor must:

- set the working directory to a non-writable application directory;
- inject configuration from a protected environment/secret source;
- restart on process failure with bounded backoff;
- send graceful termination before the hard kill timeout;
- expose only the HTTPS listener and health endpoints;
- retain structured logs without Authorization headers, raw tokens, broker
  URIs, database passwords, or signing-key contents.

Do not use `dotnet run` in production. Start the inspected API artifact from the
release manifest, for example `dotnet OpenLineOps.Api.dll --urls
https://0.0.0.0:7443`, with the TLS certificate supplied by the deployment
platform.

## Readiness, recovery, and commissioning

`/health/live` proves only that the process is alive. `/health/ready` must be
green for Runtime PostgreSQL, Station RabbitMQ, and every other configured
production dependency before traffic is admitted.

After a restart, the Coordinator rebuilds monitoring from persisted production
state and resumes only durable outbox delivery. It never replays a non-idempotent
hardware command whose outcome is uncertain. Such work remains
`RecoveryRequired` until an authenticated operator chooses Reconcile, Retry,
Abort, or Scrap.

Before enabling material arrivals, record evidence for all of the following:

1. inspected and signed release manifest;
2. PostgreSQL and RabbitMQ readiness;
3. unique Agent identities, broker ACLs, and trusted signing keys;
4. two products flowing concurrently through two Stations without lease
   conflicts;
5. vendor Passed, product Failed, crash, cancellation, and recovery routes;
6. Agent process loss and heartbeat TTL changing the topology to Offline;
7. broker outage, durable Agent result buffering, reconnect, and exactly-once
   Coordinator projection;
8. Trace artifact download with exact size and SHA-256;
9. Safe Stop and the independent machine Emergency Stop channel.

See `station-agent-deployment.md` for Station installation and
`operations-postgresql-deployment.md` for the Operations/CAP database profile.
