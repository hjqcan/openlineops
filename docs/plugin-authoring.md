# Plugin Authoring Guide

Plugins extend OpenLineOps without leaking vendor SDKs or custom execution code into the domain model.

## Plugin Boundary

The stable public contract lives in `shared/OpenLineOps.Plugin.Abstractions`.

Use these interfaces:

- `IOpenLineOpsPlugin`: common manifest, initialization, and async disposal.
- `IOpenLineOpsDeviceCommandPlugin`: device command execution.
- `IOpenLineOpsProcessNodePlugin`: process node command execution.

Do not reference application or infrastructure projects from a plugin unless a future extension contract explicitly requires it.

## Manifest

Every plugin exposes a `PluginManifest`:

- `Id`: stable unique id, for example `openlineops.samples.loopback-device`.
- `Name`: display name.
- `Version`: plugin semantic version.
- `Kind`: `DeviceDriver`, `ProcessNode`, `DataSink`, `ReportExporter`, `Integration`, or `UserInterface`.
- `EntryAssembly`: plugin assembly file name.
- `EntryType`: fully qualified plugin type name.
- `Capabilities`: capability ids exposed to the platform.
- `ContractVersion`: plugin contract version, currently `1.0.0`.
- `MinimumPlatformVersion`: minimum compatible platform version.
- `DeviceCommands` or `ProcessCommands`: command descriptors.

## Device Command Plugin

A device command plugin implements `IOpenLineOpsDeviceCommandPlugin`.

The platform calls:

1. `InitializeAsync` once before activation is accepted.
2. `ExecuteDeviceCommandAsync` for each command invocation.
3. `DisposeAsync` during unload or shutdown.

Return terminal outcomes only:

- `Completed`
- `Failed`
- `Rejected`
- `TimedOut`

Use `Rejected` for unsupported capability or invalid payload. Use `Failed` for attempted work that failed.

## Process Node Plugin

A process node plugin implements `IOpenLineOpsProcessNodePlugin`.

Process command requests include runtime context such as session id, station id, configuration snapshot id, step id, command id, node id, capability, command name, payload, and timeout.

Return terminal outcomes only:

- `Completed`
- `Failed`
- `Rejected`
- `TimedOut`
- `Canceled`

## Runtime configuration

Configuration tokens are case-sensitive and accept only these canonical values:

- `OpenLineOps:Plugins:Activator`: `ManifestOnly`, `AssemblyLoadContext`, or `ExternalProcess`.
- `OpenLineOps:Plugins:EventLog:Provider`: `Sqlite`.
- `OpenLineOps:Plugins:ExternalHost:Sandbox:IsolationMode`: `ExternalProcess`, `LeastPrivilegeIdentity`, or `Container`.

Use `ContainerRuntimeExecutable` to select `docker`, `podman`, or another compatible
runtime; executable names are not isolation-mode aliases. Invalid or empty configured
tokens fail during service registration. External-process lifecycle events always use
the configured persistent event log; there is no event-discarding sink.

Production deployments should prefer external process, container, or least-privilege
isolation for third-party plugins.

## Packaging Layout

Recommended package layout:

```text
package-root/
  manifest.json
  lib/
    OpenLineOps.SamplePlugins.LoopbackDevice.dll
    dependency.dll
  README.md
```

The manifest should match the assembly and entry type exposed by the plugin class.
Every plugin package must use the canonical `manifest.json` file name. Files with
other names are not plugin manifests and are not discovered.

## Sample

See `samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice`.

Build it with:

```powershell
dotnet build samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice/OpenLineOps.SamplePlugins.LoopbackDevice.csproj
```

The sample implements a loopback `Echo` command and demonstrates manifest declaration, validation-friendly command descriptors, cancellation handling, and terminal result outcomes.

## Review Checklist

- Manifest id, version, kind, entry assembly, entry type, and capabilities are stable.
- Command ids and capability ids are documented.
- Payload schemas are versioned when external callers depend on them.
- Initialization does not connect to devices unless the plugin is intentionally activated.
- Timeouts and cancellation tokens are respected.
- Vendor SDK exceptions are translated into plugin result outcomes.
- No vendor types appear in OpenLineOps domain or application contracts.
