# Station Agent Security Boundary

The production Station Agent is a Windows service. Its normal control plane,
RabbitMQ connection, signed-package installer, safety receiver, and SQLite
stores run under one dedicated service account. Vendor executables do not run
with that account's full token: the Station Runtime launches each one in a
Windows AppContainer and a kill-on-close Job Object.

## Service identity

Create one non-administrative local or domain account per Station. Do not use
LocalSystem, an administrator, a shared interactive operator account, or the
Coordinator identity. Windows must load a user profile for the account so that
`LOCALAPPDATA` is available; AppContainer process creation fails closed when it
is absent.

Configure the exact account name or SID in
`AllowedRestrictedExternalProgramHostAccounts` or
`AllowedRestrictedExternalProgramHostSids`. SID configuration is preferred
because it is not affected by account renaming. The Agent refuses to start when
both allowlists are empty, and the runtime refuses vendor execution when the
current identity is unauthenticated, SYSTEM, administrative, or not allowlisted.

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
    "Agent": {
      "AgentId": "agent-line-a-eol",
      "StationId": "station.eol",
      "AllowedRestrictedExternalProgramHostSids": [
        "S-1-5-21-..."
      ],
      "ExternalProgramAppContainerProfileNamespace": "OpenLineOps.LineA.Eol.ExternalPrograms"
    }
  }
}
```

## Package cache and work directories

The Agent verifies the `.olopkg` signature, canonical manifest, complete file
inventory, sizes, and SHA-256 hashes before moving content into its
content-addressed cache. On Windows, every installed cache directory disables
ACL inheritance and grants:

- SYSTEM and Administrators: full control for installation and controlled
  removal;
- the dedicated Agent service identity: owner and trusted install/cleanup
  control plane, with normal file mutation denied by the frozen ACL;
- the OpenLineOps external-program content capability SID: read and execute,
  with write, rename, delete, permission changes, and ownership changes
  explicitly denied.

The service-account and capability read grants are both required by the
AppContainer dual-principal access check. Every Job profile receives the content
capability while each vendor invocation retains its own writable workspace.
The cache boundary and every frozen object must be owned by the exact Agent
service SID. Files and directories are marked read-only. Before every Station
operation dispatch, the Agent revalidates the signed package and its complete
cached inventory, hashes, owner, ACLs, and read-only attributes. Verification
rejects an extra file, a missing file, a changed size or hash, ACL drift,
inherited permissions, or writable content. Cache deletion is performed only
by the installer's bounded content-addressed cleanup operation.

The Agent service identity is intentionally trusted: as Windows owner it can
rewrite an ACL for bounded installation cleanup, and Station Runtime currently
runs under the same host identity. Package immutability protects the Station
vendor capability and its AppContainer process; it does not claim to survive a
compromised Agent or Station Runtime. The mandatory per-dispatch verification
prevents host-identity drift or tampering from reaching the next vendor
execution, but the service account and Agent binaries remain part of the
trusted computing base.

Grant the service account modify access to the configured Agent data,
runtime-work, artifact, and SQLite directories. Do not grant the AppContainer
access to those roots. For each invocation, the host creates a unique work
directory and grants only that directory to the AppContainer. The frozen
package remains read-only.

The release bundle also contains the self-contained Python Script Worker.
Agent configuration binds Station Runtime to its absolute co-packaged path and
to one explicit sandbox policy. The production default requires least-privilege
execution through the signed co-packaged
`OpenLineOps.LeastPrivilegeLauncher.exe` and the fixed
`RestrictedCurrentLowIntegrity` identity. The launcher derives a restricted
primary token from the current Agent token, sets mandatory Low integrity (RID
4096), and starts only its sibling Script Worker. Agent startup fails closed if
the launcher is missing, redirected, a reparse point, file-identity aliased, or
configured with another identity or argument layout. It never uses `runas`, a
password, or an interactive prompt.
Container isolation is not an Agent option: the Windows worker and host Python
DLL are never represented as if they were files inside an unverified image.
`ExternalProcess` is limited to development or test configuration with required
least privilege explicitly disabled. The configured host Python runtime DLL
path is passed explicitly through the cleared Station Runtime environment;
ambient worker commands are ignored.

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

1. Confirm the Windows service runs under the configured non-administrative
   account and that its profile is loaded.
2. Confirm Broker TLS is required and the Agent trusts only the intended RSA
   public keys.
3. Install a signed package and verify from a real vendor AppContainer that
   write, rename, delete, ACL-change, and ownership-change attempts all fail.
4. Run the imported vendor-program trial with network denied, then explicitly
   opt into `internetClient` only when the protocol requires it.
5. Exercise operator cancel, timeout, Agent termination, and Safe Stop, and
   confirm no vendor descendant process remains.
6. Keep Emergency Stop on the independent Agent safety channel; vendor process
   cancellation is not a substitute for hardware safety circuitry.
