# Coordinator API security

OpenLineOps Coordinator exposes one authenticated HTTP contract. Every API and
runtime SignalR connection requires an `OpenLineOpsBearer` credential except the
minimal `/health/live` liveness probe. `/health/ready` is authenticated because
dependency readiness is operational information.

The Coordinator never accepts an Actor identity from a mutation body. It maps a
Bearer credential to one configured `ActorId` and writes that claim into
Production Run, material, recovery, alarm, and safety evidence. Trace records
have no public mutation route; they project the Actor already persisted with the
terminal Production Run evidence. Unknown
`ActorId`, `AcknowledgedBy`, `ResolvedBy`, or equivalent JSON members are rejected
by the strict request contracts.

## Provision callers

Create a separate cryptographically random token for each caller and safety
boundary. A token must be canonical unpadded base64url encoding of 32 through 64
random bytes. Store the token only in the caller's protected secret file or secret
store. Configure only its lowercase SHA-256 digest on the Coordinator:

```json
{
  "OpenLineOps": {
    "Security": {
      "Callers": [
        {
          "CredentialId": "line-a-operator",
          "ActorId": "operator.line-a",
          "TokenSha256": "<lowercase SHA-256 of the exact token>",
          "Roles": ["Operator"]
        },
        {
          "CredentialId": "line-a-safety",
          "ActorId": "safety.line-a",
          "TokenSha256": "<different lowercase SHA-256>",
          "Roles": ["Safety"]
        }
      ]
    }
  }
}
```

The only role tokens are `Engineering`, `Operator`, and `Safety`. Engineering
changes project, topology, Flow, resource, and line definitions. Operator changes
WIP, Slots, Production Runs, devices, and alarms. Trace records are query/export
only and never accept caller-supplied evidence. Emergency Stop
requires Safety; an Engineering/Operator credential receives `403 Forbidden`.
Safety is always a dedicated credential: a caller containing Safety cannot also
contain Engineering or Operator, and startup requires at least one Safety-only
caller.

Empty caller configuration, malformed or duplicate credential identities, a
duplicate token digest, an unknown role, and non-canonical values stop the host at
startup. `src/OpenLineOps.Api/appsettings.json` intentionally contains an empty
caller list and no deployable credential.

Send the token only as `Authorization: Bearer <token>`. Never place the raw token
in a URL, command line, committed appsettings file, or log. Remote Coordinator
endpoints must use HTTPS. Cleartext HTTP is accepted only over an actual loopback
connection and the host rejects a configured remote HTTP listener at startup.

## Studio and Runner

Packaged Studio provisions two independent 32-byte tokens under its per-user data
directory. The normal token has Engineering and Operator roles. The Safety token
is retained only by the Electron main process and is selected only for the exact
Emergency Stop route; it is never exposed to the renderer. Windows ACLs are
reduced to the current user and Local System and verified before either token is
read. POSIX files use mode `0600` and their directory uses `0700`.

For an explicitly provisioned installation, set both
`OPENLINEOPS_API_TOKEN_FILE` and `OPENLINEOPS_API_SAFETY_TOKEN_FILE`; Studio fails
closed if either file is missing, linked, shared, malformed, or insufficiently
protected. Both values must be absolute paths. The files and their parent
directories must be pre-created with private ACLs; external mode performs no
filesystem writes, so Studio never creates or repairs externally managed
credential paths. `OPENLINEOPS_API_ACTOR_ID` selects the canonical installation
Actor.

Studio acquires Electron's single-instance lock before it validates or creates
the canonical user-data path and before it provisions credentials. Only that
primary process may bind `data/runtime-state` or launch the local API. The API
requires the protected handshake and the canonical parent process id together,
opens a handle to that exact Electron process, and stops itself when the parent
dies; a PID lookup is not repeated after startup. Explicit backend stop also
waits for the full process tree to exit and fails closed if termination cannot
be confirmed.

Packaged databases, Trace artifacts, and external-program workspaces are all
children of one mutable `data/runtime-state` directory. The strict package
manifest lists the exact physical files, sizes, and SHA-256 values for Electron
main/renderer content, API settings and dependencies, ScriptWorker, PluginHost,
and the remaining unpacked desktop. Electron reads this inventory through
`original-fs`; ASAR's virtual directory projection cannot add or reinterpret
members. A strict marker inside the state root binds it to the resulting complete
content digest.

On a mismatch Studio creates and fsyncs a staged root, preserves the old root as
the fixed previous activation, and atomically installs the new root without yet
committing it. Only after the packaged API completes its protected handshake and
the physical package is reverified does Studio fsync the matching activation
commit, remove the previous root, and report startup success. A crash before that
commit restores the previous root; an interrupted first activation is discarded.
An absent or malformed active root is never repaired or interpreted as
compatible. Credentials and Station package keys live outside this replaceable
root.

`OpenLineOps.Runner` executes a frozen project directly and does not call the
Coordinator HTTP API, so it does not accept or forward an HTTP Bearer credential.
Any supervising process that calls Coordinator APIs must use the same Bearer
contract rather than treating Runner's `--actor-id` as HTTP authority.

## Reverse proxy boundary

Terminate TLS either in Kestrel or at an explicitly trusted reverse proxy. Do not
enable forwarded headers for arbitrary networks: configure known proxy addresses
and networks before trusting forwarded scheme or client-address values. Protect
credential hashes with the platform secret store and rotate a caller by installing
a new token and digest before removing the old credential entry.
