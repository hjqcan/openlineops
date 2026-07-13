# Windows Station Agent Deployment

The `agent` release artifact is the complete Station-side production host. It
contains the self-contained `win-x64` `OpenLineOps.Agent.exe` Windows service
with `OpenLineOps.StationRuntime.exe`, `OpenLineOps.PluginHost.exe`, and
`OpenLineOps.ScriptWorker.exe` in the same directory. The signed internal
`OpenLineOps.LeastPrivilegeLauncher.exe` is co-packaged there as well. A
Station computer does not need the Studio, the Coordinator API binaries, or a
separately installed .NET runtime. The three auxiliary entry points and the
launcher are isolated self-contained single-file hosts, so their private
dependency graphs cannot overwrite one another inside the shared Agent bundle.

Verify the outer `release-manifest.json` and `checksums.sha256` before extracting
the archive. After extraction, verify `bundle-manifest.json` and
`bundle-checksums.sha256`; their inventory must match every payload file. For a
production release, all four executable entry points and the internal least-
privilege launcher must have valid Authenticode signatures.

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

The dedicated service SID is also the exact owner of the package-cache boundary
and every frozen object. It is the trusted installation and bounded-cleanup
control plane. Vendor processes receive only the separate OpenLineOps content
capability SID, which is denied writes, deletion, ACL changes, and ownership
changes. Agent and Station Runtime compromise is outside this immutability
boundary because both use the trusted host identity; every operation therefore
revalidates the package's complete inventory, hashes, owner, ACLs, and read-only
attributes before Station Runtime is launched.

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
- an absolute `SafetyExecutablePath` for the independently reviewed,
  machine-specific actuator; it must be different from
  `OpenLineOps.StationRuntime.exe` and implement both identity-scoped Safe Stop
  and the independent Emergency Stop command contract;
- an absolute `PythonScript:HostPythonRuntimeDllPath` for the installed, supported
  64-bit Python runtime and a reviewed Python worker sandbox policy. The worker
  path remains exactly the co-packaged `OpenLineOps.ScriptWorker.exe`; do not
  point it at a mutable or separately downloaded executable. Production keeps
  `RequireLeastPrivilegeExecution` enabled, uses the exact co-packaged
  `OpenLineOps.LeastPrivilegeLauncher.exe`, and fixes the identity to
  `RestrictedCurrentLowIntegrity`. The launcher derives a restricted primary
  token from the current service token and applies mandatory Low integrity
  (RID 4096). The Agent rejects an external launcher, a different identity, a
  custom argument template, interactive prompting, `runas`, or passwords.
  `ExternalProcess` is accepted only outside the Station Agent when least
  privilege is explicitly disabled for development or testing.
  Station Agent does not accept Container isolation: its shipped worker and
  host Python DLL are Windows artifacts, and no unverified image/runtime
  configuration is exposed as a production option;
- `PluginHostExecutablePath` remains exactly `OpenLineOps.PluginHost.exe`. The
  Agent resolves this co-packaged executable to an absolute path before it
  clears and constructs the Station Runtime environment. Station Runtime
  discovers plugins only from the verified request release's content-addressed
  `packages` directory, requires exact identity and hash agreement with the
  release lock, and explicitly starts and stops each plugin through this host.
  A machine-level or repository-level plugin directory is never consulted;

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
binary. `SafetyExecutablePath` is a required deployment setting and must name
an independently reviewed actuator executable that implements the strict
`safe-stop` and `emergency-stop` command contract. The Agent rejects the
Station Runtime executable as a safety actuator; it does not pretend that a
generic automation runtime can make machine-specific hardware safe.

Install the supported x64 Python runtime on the Station and set
`PythonScript:HostPythonRuntimeDllPath` to its canonical absolute DLL path, for
example `C:\Program Files\Python312\python312.dll`. The Agent passes only this
validated path, the absolute co-packaged worker path, and the typed sandbox
policy into Station Runtime's cleared environment. Station Runtime does not
read a mutable machine-level worker command or fall back to in-process Python.

Release CI validates this boundary with
`eng/verify-staged-agent-bundle-e2e.ps1`. The script extracts the Agent ZIP from
`artifacts/release`, executes all four bundled entry points, and runs signed
plugin and Python `.olopkg` process chains through the extracted executables.
Its Python execution uses the exact bundled launcher and fixed
`RestrictedCurrentLowIntegrity` policy. The Python child queries its own token;
the gate fails unless `IsTokenRestricted` is true and the integrity RID is
4096. Commissioning evidence for a real Station must additionally include the
live RabbitMQ-connected Agent service run.

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
