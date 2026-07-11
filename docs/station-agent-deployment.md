# Windows Station Agent Deployment

The `agent` release artifact is the complete Station-side production host. It
contains the self-contained `win-x64` `OpenLineOps.Agent.exe` Windows service
and `OpenLineOps.StationRuntime.exe` in the same directory. A Station computer
does not need the Studio, the Coordinator API binaries, or a separately
installed .NET runtime.

Verify the outer `release-manifest.json` and `checksums.sha256` before extracting
the archive. After extraction, verify `bundle-manifest.json` and
`bundle-checksums.sha256`; their inventory must match every payload file. For a
production release, both executable entry points must have valid Authenticode
signatures.

## Service account and directories

Create a dedicated non-administrative local or domain account for each Station.
Do not use LocalSystem, an administrator, an interactive operator, the
Coordinator identity, or an account shared by multiple Stations. Configure the
Windows service to load the account profile. Record its stable SID and place
that SID in `AllowedRestrictedExternalProgramHostSids`; the Agent fails closed
when neither an account nor SID allowlist is configured.

Install the bundle in a read-only program directory such as
`C:\Program Files\OpenLineOps\StationAgent`. Grant the service account read and
execute access there. Create separate writable directories for:

- Agent state and SQLite (`DataDirectory`);
- signed `.olopkg` distribution (`PackageDistributionDirectory`);
- content-addressed package cache (`PackageCacheDirectory`, when overridden);
- Station Runtime work (`RuntimeWorkingDirectory`, when overridden);
- retained trace artifacts (`ArtifactDirectory`, when overridden);
- artifact exchange with the Coordinator (`ArtifactExchangeDirectory`);
- independent safety work (`SafetyWorkingDirectory`, when overridden).

Grant only the service account, SYSTEM, and the intended administrators the
required access. Vendor AppContainer identities receive access only to their
per-invocation workspace and verified immutable package content, never the Agent
state, package distribution, or artifact roots.

## Required configuration

Edit the bundled `appsettings.json` before installing the service. Empty values
are intentional fail-fast placeholders. At minimum configure:

- a unique `AgentId` and its exact `StationId`;
- an `amqps://` `BrokerUri` with `RequireBrokerTls` set to `true` and a broker
  certificate trusted by Windows;
- package distribution and artifact exchange directories;
- `MaterialArrivalPackageContentSha256` as the exact lowercase SHA-256 of the
  active signed package. The Agent opens
  `<PackageDistributionDirectory>\<sha256>.olopkg`, verifies its signature,
  complete inventory, and content hash, and derives Project, Application,
  Snapshot, Line, and Station identity only from that frozen manifest;
- a canonical `MaterialArrivalPipeName` unique to the Station service. The
  named pipe uses `PipeOptions.CurrentUserOnly`, so a scanner, PLC bridge, or
  manual-entry adapter must run under the same dedicated Windows service
  account to submit arrival signals;
- one or more trusted RSA public-key files in
  `TrustedPackagePublicKeyFiles`, keyed by the exact release signing key ID;
- an allowed restricted-host account or SID;
- a stable `ExternalProgramAppContainerProfileNamespace`;

The arrival adapter sends the local signal before the material is offered to a
Station operation. The Agent first verifies the active signed package, binds the
signal to its frozen deployment identity, validates the message, and commits it
to the SQLite material-arrival outbox. Only then does the local IPC response say
`Accepted`. RabbitMQ publication happens from that durable outbox. Broker
outages apply bounded retry; Agent restart or an adapter disconnect after
submission does not lose the event, and the same MessageId/IdempotencyKey is
replayed without creating a second arrival.

`RuntimeExecutablePath` must remain exactly
`OpenLineOps.StationRuntime.exe`. Relative paths resolve from the Agent bundle
directory, so the default selects the Station Runtime shipped in and verified
with the same bundle. Do not point it to a mutable or separately downloaded
binary. `SafetyExecutablePath` defaults to the same verified runtime unless an
explicit independently reviewed safety actuator is configured.

## Windows service installation

From an elevated PowerShell prompt, create the service with an absolute binary
path and the dedicated account:

```powershell
$agent = 'C:\Program Files\OpenLineOps\StationAgent\OpenLineOps.Agent.exe'
sc.exe create OpenLineOpsStationAgent binPath= ('"' + $agent + '"') start= auto obj= 'DOMAIN\station-line-a' password= '<service-account-password>'
sc.exe failure OpenLineOpsStationAgent reset= 86400 actions= restart/5000/restart/15000/none/0
sc.exe start OpenLineOpsStationAgent
```

Store the service password through the organization's managed service-account or
credential process; never place it in `appsettings.json`, scripts, the package
directory, logs, or release evidence. Validate the service identity, loaded
profile, broker TLS peer, trusted signing-key IDs, directory ACLs, and package
cache immutability before accepting production work.

Operational recovery and Emergency Stop remain distinct. A process crash or
uncertain non-idempotent action enters `RecoveryRequired` and is not replayed.
Emergency Stop uses the independent Agent safety channel and does not replace
machine safety circuitry.
