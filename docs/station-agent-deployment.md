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

## Service identity and directories

Install every Station as a uniquely named SCM service under the built-in
`NT AUTHORITY\LocalService` account (`S-1-5-19`) and set its service SID type to
`Restricted`. The LocalService account is intentionally shared; Station
isolation comes from the distinct `S-1-5-80-...` SID derived by Windows from
each service name. Set that exact SCM name in `OpenLineOps:WindowsServiceName`;
it is the sole service-identity configuration source, and the Agent derives the
SID itself. Startup verifies LocalService as the token user, enabled
non-deny-only service-logon SID `S-1-5-6`, a restricted token, and the derived
service SID as both enabled and restricted. Do not use LocalSystem, an
administrator, an interactive operator, the Coordinator identity, or a
separately managed service account.

The deployment gate also requires `TokenElevationTypeDefault`, meaning that the
service token has no UAC linked token. `TokenElevation` is deliberately not used
to classify the service as administrative; the exact LocalService SID, absence
of the Administrators group, non-LocalSystem user, and restricted service-SID
facts are the authoritative access identity evidence.

Install the bundle in a read-only program directory such as
`C:\Program Files\OpenLineOps\StationAgent`. Grant the exact service SID read and
execute access there. Create separate writable directories for:

- Agent state and SQLite (`DataDirectory`);
- signed `.olopkg` distribution (`PackageDistributionDirectory`);
- Station Runtime work (`RuntimeWorkingDirectory`, when overridden);
- retained trace artifacts (`ArtifactDirectory`, when overridden);
- independent safety work (`SafetyWorkingDirectory`, when overridden).

Grant only the exact service SID, SYSTEM, and the intended administrators the
required access. Vendor AppContainer identities receive access only to their
per-invocation workspace and verified immutable package content, never the Agent
state, package distribution, or artifact roots.

`PackageCacheDirectory` is different: it is mandatory, has no fallback under
`DataDirectory`, and must be the sole child of a dedicated immutable namespace
anchor. For example, configure the cache root as
`C:\ProgramData\OpenLineOps\StationCaches\LineA-Eol\content-anchor\content`;
`content-anchor` may contain only `content`. Put data, work, artifacts,
distribution, logs, and every other mutable directory outside that anchor. Do
not grant the Station service deletion or permission/ownership-change authority
over the anchor or cache root. The normal Agent does not create, repair, or
relax this namespace.

## Provision the content-cache namespace

Run the bundled provisioning command once from an elevated PowerShell process
before the service is started. The command accepts only the normal configuration
keys; it does not introduce a second service SID or cache-path setting:

```powershell
$serviceName = 'OpenLineOpsStationAgent-LineA-Eol'
$agent = 'C:\Program Files\OpenLineOps\StationAgent\OpenLineOps.Agent.exe'
$cacheRoot = 'C:\ProgramData\OpenLineOps\StationCaches\LineA-Eol\content-anchor\content'

& $agent --provision-content-cache `
  --OpenLineOps:WindowsServiceName $serviceName `
  --OpenLineOps:Agent:PackageCacheDirectory $cacheRoot
if ($LASTEXITCODE -ne 0) {
  throw "Station content-cache provisioning failed with exit code $LASTEXITCODE."
}
```

The caller must be LocalSystem or an elevated member of Administrators. The
cache path must already be an absolute canonical local drive path on a ready
fixed NTFS volume. One trailing separator is normalized; relative, dot-segment,
repeated-separator, UNC, device, network, removable, and non-NTFS paths are
rejected; they cannot provide the local lock, file-identity, stream, and ACL
guarantees used by the installer.
The command fails on non-Windows hosts and under a non-elevated token. It derives
the exact service SID from `WindowsServiceName`, derives the same external
program content capability used during execution, and creates or verifies the
anchor and cache root with their exact protected ACLs. It rejects a relative or
noncanonical namespace, reparse points, alternate data streams, unexpected
anchor children, owner or ACL drift, and unsafe pre-existing content. Repeating
the command with the same inputs is idempotent; changing Station identity or
moving an existing cache is an explicit administrator recovery operation.
Provisioning is permitted only while the named service is absent (first
deployment) or fully `Stopped` with no process. `StartPending`, `Running`,
`StopPending`, paused, or any other installed state is rejected inside the
content-protection API, closing the race between namespace changes and package
installation.

Persist the same `WindowsServiceName` and absolute `PackageCacheDirectory` in
the service configuration. Normal startup only verifies this provisioned
boundary and fails closed if it is absent or drifted. A package commit marker
records installation transaction state only; it carries no package trust or
provenance and is never treated as an authentication credential. The signed
package, manifest, complete inventory, hashes, immutable ACLs, and exact marker
are all revalidated before use.

Protected package removal is also an explicit administrator operation; never
delete a hash directory or its transaction marker manually. Stop the installed
service, wait for the exact SCM state, and pass the lowercase package content
SHA-256 to the same signed Agent entry point:

```powershell
$serviceName = 'OpenLineOpsStationAgent-LineA-Eol'
$agent = 'C:\Program Files\OpenLineOps\StationAgent\OpenLineOps.Agent.exe'
$cacheRoot = 'C:\ProgramData\OpenLineOps\StationCaches\LineA-Eol\content-anchor\content'
$contentSha256 = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'

Stop-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus(
  [System.ServiceProcess.ServiceControllerStatus]::Stopped,
  [TimeSpan]::FromSeconds(30))
& $agent --remove-content-cache-package $contentSha256 `
  --OpenLineOps:WindowsServiceName $serviceName `
  --OpenLineOps:Agent:PackageCacheDirectory $cacheRoot
if ($LASTEXITCODE -ne 0) {
  throw "Protected Station package removal failed with exit code $LASTEXITCODE."
}
```

Removal requires an installed, fully stopped service; unlike first-time
provisioning, a missing SCM service is rejected. Under the same cross-process
cache lock, the operation revalidates the name-to-SID binding, namespace, exact
marker record and hash, plus the content directory's type, immutable ACL,
stream, and single-link provenance. It then removes the marker before content
and fails closed on uncertainty. Removal does not claim to reconstruct or
rehash a package inventory from a content hash alone. Provision and removal
switches are mutually exclusive.

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
- an absolute canonical local fixed-NTFS `PackageCacheDirectory`, explicitly
  provisioned as described above; the package
  distribution directory, plus an HTTPS `CoordinatorBaseUri` for
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
- the material-arrival pipe name is not configurable. The Agent derives it
  deterministically from the exact Station service SID, preventing same-host
  Station collisions and name drift. Its protected DACL and owner contain only
  that SID, it uses first-instance protection, and it remains open for the
  Agent lifetime. An in-process scanner/PLC adapter, or a child adapter launched
  with the Station service token, may submit arrivals. An independent bridge
  must use the authenticated Coordinator API; sharing LocalService is not
  authorization;
- one or more trusted RSA public-key files in
  `TrustedPackagePublicKeyFiles`, keyed by the exact release signing key ID;
- the exact unique SCM service name in `OpenLineOps:WindowsServiceName`; it must
  match the installed service name and is the sole source of the derived
  restricted service SID;
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
the canonical runtime root, DLL, and capability SID. The Agent service identity
does not need and must not receive `WRITE_DAC` on the Python installation. At run
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
and stale-profile recovery. Its staged RabbitMQ child gate installs the exact
extracted Agent as a demand-start `WIN32_OWN_PROCESS` service named
`OpenLineOpsAgentE2E-<32-lowercase-hex-guid>` under
`NT AUTHORITY\LocalService`, configures `SERVICE_SID_TYPE_RESTRICTED`, and
supplies test configuration through the service-specific registry
`Environment` value outside the frozen
bundle. The service key ACL is limited to SYSTEM, administrators, and the test
principal; the broker credential is never written to the service `ImagePath`,
command line, bundle, or evidence. The gate starts, stops, and restarts the
Agent through Windows SCM, stops the real broker while the signed frozen vendor
helper is executing, proves the terminal result is durable in SQLite, restarts
the broker, proves exactly-once result delivery, and proves the hardware action
is not replayed after the service restart. Cleanup deletes the temporary
service, service environment, EventLog source, copied owned root, and any
remaining service process. It never creates or deletes an account, profile, or
service-logon grant. Commissioning evidence must also include the
deployment-specific TLS peer, per-Station broker ACL, heartbeat Offline/Online
transition, and independent safety actuator.

Each formal CI invocation allocates an independent 32-hex service scope before
the test starts. A protected manifest under the runner's private temporary root
binds only `role`, `serviceSuffix`, `serviceName`, the fixed LocalService name
and SID, the derived service SID, `serviceSidType=Restricted`, the copied Agent
path and hash, the exact Windows Temp owned root, and the exact role-specific
`CommonApplicationData` (`%ProgramData%`) package-cache root beneath its
deterministic anchor. The manifest has no
broker URI, token, password, account lifecycle, profile, logon-right, or
arbitrary cleanup path. Both the wrapper `finally` block and a separate
`if: always()` workflow step invoke the same bounded cleanup Fact; a missing
manifest is accepted only after the exact deterministic service, process,
registry key, EventLog source, and owned root are proven absent.

CI also runs an external-abort proof under a third independent scope. After SCM
reports the Agent Running and the protected marker binds the testhost PID,
Agent PID, image path and hash, the wrapper snapshots and force-terminates the
complete `dotnet test` driver tree, including all testhost descendants. It
proves every snapshotted child is gone while the SCM-hosted Agent remains
alive, then launches cleanup
from another `dotnet test` process, and verifies the process, service,
per-service environment, EventLog source, ACLs, and owned root are gone. The
cleanup Fact then runs again against
the same retained manifest to prove idempotency; the workflow-level always step
is the final timeout/crash safety net.

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
after Agent restart, the canonical temporary SCM service name, a fully verified
start/stop/restart/delete lifecycle, the same LocalService base identity and
exact restricted service SID before and after restart, presence Offline/Online
transitions, and a clean shutdown. `-RequireSanitizedRoot`
additionally rejects reparse points, credentials, private keys, extracted
runtime payloads, token diagnostics, logs, and any file outside the exact
top-level evidence, five TRX files, and raw RabbitMQ evidence references.

## Windows service installation

From an elevated PowerShell prompt, create the service with an absolute binary
path, the fixed LocalService account, and a restricted service SID. Provision
the dedicated content-cache namespace before the first service start:

```powershell
$agent = 'C:\Program Files\OpenLineOps\StationAgent\OpenLineOps.Agent.exe'
$serviceName = 'OpenLineOpsStationAgent-LineA-Eol'
$cacheRoot = 'C:\ProgramData\OpenLineOps\StationCaches\LineA-Eol\content-anchor\content'
& $agent --provision-content-cache `
  --OpenLineOps:WindowsServiceName $serviceName `
  --OpenLineOps:Agent:PackageCacheDirectory $cacheRoot
if ($LASTEXITCODE -ne 0) { throw 'Content-cache provisioning failed.' }

sc.exe create $serviceName binPath= ('"' + $agent + '"') start= auto obj= 'NT AUTHORITY\LocalService'
sc.exe sidtype $serviceName restricted
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/none/0
sc.exe start $serviceName
```

The service has no managed password or per-Station account lifecycle. Validate
the LocalService account SID, the exact enabled-and-restricted service SID,
broker TLS peer, trusted signing-key IDs, directory ACLs, and package-cache
immutability before accepting production work.

Operational recovery and Emergency Stop remain distinct. A process crash or
uncertain non-idempotent action enters `RecoveryRequired` and is not replayed.
Emergency Stop uses the independent Agent safety channel and does not replace
machine safety circuitry.
