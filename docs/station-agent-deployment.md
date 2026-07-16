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

- a unique `AgentId`, its exact physical `StationId`, and the frozen topology
  `StationSystemId` it is allowed to control. Jobs, leases, safety commands,
  and presence reports with another Station System identity are rejected;
- `HeartbeatInterval` as a constant-format duration no greater than the
  Coordinator presence TTL. The Agent publishes one session-scoped Started
  report and monotonically sequenced heartbeats; a missing/expired presence is
  shown as Offline in the production-line topology;
- an `amqps://` `BrokerUri` with `RequireBrokerTls` set to `true` and a broker
  certificate trusted by Windows. Use a unique broker principal for this Agent,
  restrict reads to this exact Station's queues, restrict writes to Station
  event exchanges, and restrict topic writes to the SHA-256 Station routing
  segment. A shared Agent credential, wildcard ACL, or production `guest`
  account is invalid. With `RequireBrokerTls=true`, Agent startup fails closed
  unless the URI contains a non-`guest` username and a non-empty password;
  credentialless broker URIs are reserved for explicit
  `RequireBrokerTls=false` local/integration-test execution;
- package distribution directories, plus an HTTPS `CoordinatorBaseUri` for
  central Trace upload. Provision `ArtifactUploadBearerToken` from the service
  secret store or environment at startup; never place it in the bundled
  `appsettings.json`, command line, diagnostics, or logs. The credential has
  only the `StationAgent` role, its Actor ID is the exact `AgentId`, and its
  Station claim is the exact physical `StationId`. Plain HTTP is accepted only
  for a loopback Coordinator used by local commissioning tests;
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
  `PerExecutionAppContainer`. Each execution receives a unique AppContainer
  profile, package SID, protected read-only Worker content directory, and
  package-private writable runtime directory. Windows supplies Low integrity
  (RID 4096), and the launch policy supplies no network capability. The Worker
  is created suspended, assigned to a kill-on-close Job Object, and resumed
  only after the security boundary is active. Profile deletion removes the
  complete runtime tree; a locked marker under
  `%LOCALAPPDATA%\OpenLineOps\ScriptWorker` prevents concurrent cleanup, and
  the next launch recovers only canonical stale markers left by a crash. The
  Agent rejects an external launcher, a different identity, a
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
After every Python install or upgrade, provision the installation once from an
elevated PowerShell prompt with the signed launcher from the Agent bundle:

```powershell
& 'C:\Program Files\OpenLineOps\StationAgent\OpenLineOps.LeastPrivilegeLauncher.exe' provision-python-runtime --runtime-dll 'C:\Program Files\Python312\python312.dll'
```

The command recursively rejects reparse points, grants read-and-execute only
to the stable `OpenLineOps.pythonRuntime` capability, and emits JSON containing
the canonical runtime root, DLL, and capability SID. The service account does
not need and must not receive `WRITE_DAC` on the Python installation. At run
time the launcher only verifies the inheritable capability ACE; a missing or
drifted provision fails closed with the exact provisioning command instead of
modifying `Program Files` or falling back to another Python runtime.

Release CI validates this boundary with
`eng/verify-staged-agent-bundle-e2e.ps1`. The script extracts the Agent ZIP from
`artifacts/release`, executes all four bundled entry points, and runs signed
plugin and Python `.olopkg` process chains through the extracted executables.
Its Python execution uses the exact bundled launcher and fixed
`PerExecutionAppContainer` policy. The Python child queries its own token; the
gate fails unless `TokenIsAppContainer` is true, the package SID is a canonical
unique AppContainer SID, and the integrity RID is 4096. It also verifies
cross-profile denial, network denial, descendant termination, profile deletion,
and stale-profile recovery. Its staged RabbitMQ child gate runs the extracted
Agent against a signed frozen vendor helper, stops the real broker while the
helper is executing, proves the terminal result is durable in SQLite, restarts
the broker, proves exactly-once result delivery, then restarts the Agent and
proves the hardware action is not replayed. Commissioning evidence must also
include the deployment-specific TLS peer, per-Station broker ACL, heartbeat
Offline/Online transition, and independent safety actuator.

For an already staged local candidate, run the exact production gate from the
repository root after a real local broker is ready:

```powershell
$env:OPENLINEOPS_RABBITMQ_URI = "amqp://guest:guest@127.0.0.1:5672/%2f"
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-staged-agent-bundle-e2e.ps1 -Configuration Release -NoBuild -NoRestore
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-staged-agent-evidence.ps1 -EvidenceRoot output/staged-agent-bundle-e2e
```

There is no transport-skip mode. The evidence validator requires completed and
passed vendor execution, authenticated central HTTP Artifact receipts and
Operator GET/hash checks, all five Artifact kinds, durable offline Outbox
state, exactly-once completion after reconnect, duplicate rejection before and
after Agent restart, distinct non-administrative process identities, presence
Offline/Online transitions, and a clean shutdown. `-RequireSanitizedRoot`
additionally rejects reparse points, credentials, private keys, extracted
runtime payloads, token diagnostics, logs, and any file outside the exact
top-level evidence, five TRX files, and raw RabbitMQ evidence references.

## Windows service installation

From an elevated PowerShell prompt, create the service with an absolute binary
path and the dedicated account:

```powershell
$agent = 'C:\Program Files\OpenLineOps\StationAgent\OpenLineOps.Agent.exe'
sc.exe create OpenLineOpsStationAgent binPath= ('"' + $agent + '"') start= auto obj= 'DOMAIN\station-line-a' password= '<service-account-password>'
sc.exe sidtype OpenLineOpsStationAgent unrestricted
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
