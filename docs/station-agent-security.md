# Station Agent Security Boundary

The production Station Agent is a Windows service. Its normal control plane,
RabbitMQ connection, signed-package installer, safety receiver, and SQLite
stores run under the Station's restricted LocalService identity. Vendor
executables do not run under an unrestricted host token: the Station Runtime launches each one in a
Windows AppContainer and a kill-on-close Job Object.

## Service identity

Run every Station service under `NT AUTHORITY\LocalService` (`S-1-5-19`) and
configure SCM `SERVICE_SID_TYPE_RESTRICTED`. Stations share that built-in base
account but have distinct service names and therefore distinct
`S-1-5-80-...` service SIDs. Startup must fail unless the token contains the
service-logon SID and the exact service SID as enabled entries, with the exact
service SID also present in the restricted SID list. Do not use LocalSystem, an
administrator, an interactive operator, the Coordinator identity, or a custom
per-Station account.

Configure only the exact SCM service name in `OpenLineOps:WindowsServiceName`.
The Agent derives its `S-1-5-80-...` SID from that name and refuses to start
unless the token user is LocalService, the service-logon SID `S-1-5-6` is
enabled and non-deny-only, the token reports restrictions, and the derived
service SID is both enabled and present in the restricted SID list. There is no
independent service-SID configuration value that can drift from the SCM name.

## Station-local IPC

Material-arrival and resource-fence named pipes are owned by, and grant access
only to, the exact service SID derived from `WindowsServiceName`. Their DACLs
are protected from inheritance, clients verify the owner and complete ACL
before sending data, and `FirstPipeInstance` prevents name preemption. Each
server keeps its first instance for its complete lifetime and disconnects only
between bounded requests, so another process cannot occupy the name during a
recreation window.

The resource-fence authority also requires a per-Job cryptographically random
256-bit token and exact Job, run, operation, and lease evidence. A response is
not released for reuse until the Station Runtime confirms that it read the
complete frame; an incomplete caller times out. The token is defense in depth,
not a substitute for the service-SID ACL. A second service running under the
same LocalService base account cannot connect because it has a different
restricted service SID.

A scanner or PLC bridge may use the local arrival pipe only when it is hosted
inside the Agent or deliberately launched as its child with the same Station
service token. An independently managed adapter must use the authenticated
Coordinator arrival endpoint. Running a separate process merely as
LocalService never grants Station-local IPC access.

Use a stable deployment-specific `ExternalProgramAppContainerProfileNamespace`
of at most 128 characters without leading, trailing, or control whitespace.
The Agent derives one deterministic AppContainer profile from the namespace,
Agent identity, Station identity, and persisted Job identity. All vendor actions
inside that Job share the profile, so a command cannot delete another concurrent
command's security identity. The Agent owns the profile and deletes it only after
the complete Station Runtime tree exits. If the Agent is terminated first, its
kill-on-close Job Object ends the process tree; startup recovery then deletes
profiles only for this Agent's persisted `RecoveryRequired` Jobs. There is no
machine-wide prefix scan.

```json
{
  "OpenLineOps": {
    "WindowsServiceName": "OpenLineOpsAgent-LineA-Eol",
    "Agent": {
      "AgentId": "agent-line-a-eol",
      "StationId": "station.eol",
      "StationSystemId": "station.system.eol",
      "HeartbeatInterval": "00:00:05",
      "PackageCacheDirectory": "C:\\ProgramData\\OpenLineOps\\StationCaches\\LineA-Eol\\content-anchor\\content",
      "ExternalProgramAppContainerProfileNamespace": "OpenLineOps.LineA.Eol.ExternalPrograms"
    }
  }
}
```

## Provisioned package-cache namespace

`PackageCacheDirectory` is required and has no implicit `DataDirectory/content`
fallback. Its immediate parent is a dedicated namespace anchor containing only
that cache root. It must use an absolute canonical local drive path on a ready
fixed NTFS volume; one trailing separator is normalized, while relative,
dot-segment, repeated-separator, UNC, device, network, removable, and non-NTFS
paths are invalid. A LocalSystem or elevated Administrator provisions it before
normal service startup with the signed bundle entry point:

```powershell
& 'C:\Program Files\OpenLineOps\StationAgent\OpenLineOps.Agent.exe' --provision-content-cache `
  --OpenLineOps:WindowsServiceName 'OpenLineOpsStationAgent-LineA-Eol' `
  --OpenLineOps:Agent:PackageCacheDirectory 'C:\ProgramData\OpenLineOps\StationCaches\LineA-Eol\content-anchor\content'
```

Provisioning derives both the Station service SID and the external-program
content capability SID; neither is accepted as an independent deployment
input. It creates or verifies the protected anchor and cache ACLs, rejects
reparse points, alternate data streams and unknown anchor children, and is
idempotent only for the exact same namespace and identities. The runtime
service never provisions or repairs the namespace. Missing or drifted
protection therefore stops startup or package installation instead of turning
an execution identity into an ACL administrator.

Provisioning also queries SCM and proceeds only when the exact configured
service is absent or fully stopped. Every installed or pending non-stopped state
is rejected. Protected package removal has a separate strict command,
`--remove-content-cache-package <lowercaseSha256>`; it requires the service to
exist and be fully stopped, shares the cache-wide cross-process lock, and
revalidates the exact marker record and hash plus the content namespace, type,
immutable ACL, streams, and single-link provenance. It then deletes the marker
before content. It does not rehash an unavailable package inventory from the
content hash alone. The provisioning and removal modes are mutually exclusive,
require an elevated Administrator or LocalSystem, and are never reachable
through normal service execution.

The Agent verifies the `.olopkg` signature, canonical manifest, complete file
inventory, sizes, and SHA-256 hashes before moving content into its
content-addressed cache. The dedicated anchor prevents the Station token from
renaming or replacing the cache root through parent-directory authority. On
Windows, the cache boundary and every installed object disable ACL inheritance
and apply exact grants and denials:

- SYSTEM and Administrators: full control for installation and controlled
  removal;
- the exact Agent service SID: the minimum create/read/execute authority needed
  for transactional installation, with frozen mutation, deletion, permission
  changes, ownership changes, and cache-root replacement denied;
- the OpenLineOps external-program content capability SID: read and execute,
  with write, rename, delete, permission changes, and ownership changes
  explicitly denied.

The service-SID and capability read grants are both required by the
AppContainer dual-principal access check. Every Job profile receives the content
capability while each vendor invocation retains its own writable workspace.
The administrator-owned anchor and cache boundary use their exact protected
ACLs. Frozen content and marker objects are Station-owned. A cleanup transition
preserves its proven Station-service or LocalService pre-transition owner. Both
states use an exact OWNER RIGHTS deny so filesystem ownership cannot restore
implicit permission-change or ownership-change authority to the Station token.
Frozen files and directories are marked read-only. Before every Station
operation dispatch, the Agent revalidates
the signed package, transaction marker, complete cached inventory, hashes,
owners, ACLs, link counts, streams, stable file identities, and read-only
attributes. Verification rejects an extra or missing object, size or hash
change, ACL drift, inheritance, a hard-link alias, an alternate stream, a
reparse point, or writable content. A commit marker records transaction state,
carries no package trust or provenance, and never substitutes for signature or
content verification. Administrative
cleanup uses a separately authorized, bounded path and does not give the normal
service token permission to rewrite a frozen object's ACL.

Grant the exact service SID modify access to the configured Agent data,
runtime-work, artifact, and SQLite directories. Do not grant the AppContainer
access to those roots. For each invocation, the host creates a unique work
directory and grants only that directory to the AppContainer. The frozen
package remains read-only.

The release bundle also contains the self-contained Python Script Worker.
Agent configuration binds Station Runtime to its absolute co-packaged path and
to one explicit sandbox policy. Production requires the signed co-packaged
`OpenLineOps.LeastPrivilegeLauncher.exe` and the fixed
`PerExecutionAppContainer` identity.

Every invocation creates a unique AppContainer profile and package SID. The
Worker payload is copied from the verified sibling executable into that
profile: its content directory has a protected read-and-execute ACL while its
runtime directory grants modify access only to the current package SID, the
trusted service identity, and SYSTEM. Windows supplies the AppContainer Low
integrity token (RID 4096). No network capability is present. The stable
`OpenLineOps.pythonRuntime` capability receives read-and-execute access to the
reviewed Python installation and is the only additional Worker capability.
That access is installed explicitly after Python installation or update:

```powershell
& 'C:\Program Files\OpenLineOps\StationAgent\OpenLineOps.LeastPrivilegeLauncher.exe' provision-python-runtime --runtime-dll 'C:\Program Files\Python312\python312.dll'
```

This command is an installer/administrator operation whose caller must already
have permission to change the Python tree DACL. Normal Agent execution never
changes that DACL: it verifies the inheritable capability rule and fails closed
with an actionable provisioning error if the rule is absent. Consequently the
Station service identity requires neither elevation nor `WRITE_DAC` over the
host Python installation.

The launcher creates the Worker suspended with
`PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`, passes only the standard-stream
handles and its explicit environment, assigns the process to an unnamed
kill-on-close Job Object, and resumes it only after assignment. Worker exit,
cancellation, launcher termination, or Agent failure closes the Job and kills
every descendant before profile cleanup. Deleting the AppContainer profile is
the cleanup authority for its complete writable tree, including read-only,
deep, and wide content; there is no custom recursive delete path.

The protected `%LOCALAPPDATA%\OpenLineOps\ScriptWorker` directory contains
only locked active-profile markers. A normal exit deletes the profile and
marker. After a crash, the next launcher can exclusively open only stale
markers, deletes their exact canonical profiles, and never scans or deletes
another namespace. Locked markers protect concurrent executions. Cleanup
failure returns launcher failure and retains the marker for recovery instead
of hiding residue behind a successful result.

Agent startup fails closed if the launcher is missing, redirected, a reparse
point, file-identity aliased, or configured with another identity or argument
layout. It never uses `runas`, a password, or an interactive prompt.
Container isolation is not an Agent option: the Windows worker and host Python
DLL are never represented as files inside an unverified image.
`ExternalProcess` is limited to development or test configuration with required
least privilege explicitly disabled. The configured host Python runtime DLL
path is passed explicitly through the cleared Station Runtime environment;
ambient worker commands are ignored. Windows rewrites standard AppContainer
`TEMP` and `LOCALAPPDATA` locations into the profile. OpenLineOps therefore
uses a dedicated per-execution runtime-root variable and keeps the bundle
extraction root inside the same profile.

On Windows, the native launcher supports a frozen executable whose absolute
path exceeds `MAX_PATH` by passing an extended-length application name directly
to `CreateProcessW`. Windows does not provide the same guarantee for the process
current directory. Configure `WorkspaceRootPath` so that the root plus the
32-character invocation directory remains shorter than 260 characters; the
Agent validates this before accepting work. Do not use short-path aliases,
junctions, or a machine-wide long-path policy as a substitute.

## Process and network isolation

The Windows launcher creates the vendor process suspended, passes only the
three standard-stream handles and the exact allowed environment block, assigns
the process to its Job Object, and resumes it only after all restrictions are
active. Closing the Job terminates the complete process tree on cancellation,
timeout, Agent failure, or host failure. Process count, per-process memory, Job
memory, CPU time, wall-clock time, output bytes, artifact count, artifact size,
and directory depth are bounded by the intersection of host and resource
limits.

The `Restricted` permission profile has two explicit network choices:

- `NetworkAccessAllowed: false` supplies no network capability. Windows denies
  network access.
- `NetworkAccessAllowed: true` supplies only the AppContainer
  `internetClient` capability. This is a high-risk opt-in. It does not grant
  local/private-network server access or broader Windows capabilities.

Network isolation is attached to the vendor process token. It does not use a
machine-wide firewall rule and therefore cannot block the Agent's RabbitMQ or
Coordinator traffic. Provider plugins for REST, TCP, file exchange, and COM
remain separate adapters; an Application executable receives only the
permissions frozen in its resource definition.

## Operational checks

Before enabling production work on a Station:

1. Confirm the Windows service runs as LocalService, its service SID type is
   `Restricted`, and its exact service SID is enabled and restricted in the
   process token.
2. Confirm Broker TLS is required and the Agent trusts only the intended RSA
   public keys.
3. Run `--provision-content-cache` elevated, start the real restricted service,
   and verify with that service token that cache-root replacement and frozen
   write, rename, delete, ACL-change, ownership-change, hard-link, and
   alternate-stream attempts all fail while reads succeed.
4. Stop the service, remove a retired package only through
   `--remove-content-cache-package <lowercaseSha256>`, and prove the command
   rejects running, pending, missing-service, malformed-hash, and drifted-cache
   states.
5. Run the imported vendor-program trial with network denied, then explicitly
   opt into `internetClient` only when the protocol requires it.
6. Exercise operator cancel, timeout, Agent termination, and Safe Stop, and
   confirm no vendor descendant process remains.
7. Keep Emergency Stop on the independent Agent safety channel; vendor process
   cancellation is not a substitute for hardware safety circuitry.
