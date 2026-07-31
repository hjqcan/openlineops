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

## Application ownership and import

Plugins are owned by one Application. They are never discovered from a global
directory. In Studio, use **Extensions > Import package** and select a ZIP whose
root contains `manifest.json` and the complete plugin payload. Import validates
the manifest, rejects unsafe archive paths, computes every file hash and the
canonical full-tree SHA-256, then commits both of these Application-local
artifacts atomically:

```text
applications/<application>/
  <application>.oloapp
  plugins/
    <portable-id>/
      manifest.json
      <entry assembly and dependencies>
```

The `.oloapp` file is authoritative and contains one strict reference per
package:

```json
{
  "pluginPackageReferences": [
    {
      "pluginId": "openlineops.samples.loopback-device",
      "version": "1.0.0",
      "manifestPath": "plugins/loopback-device/manifest.json",
      "contentSha256": "<lowercase full-tree sha256>"
    }
  ]
}
```

Do not copy a package into `plugins/` by hand. An unreferenced directory is not
installed, and a missing or modified referenced file makes validation and
publication fail. Copying the complete Application directory to another
Project preserves the package without rewriting files.

## Runtime configuration

Configuration tokens are case-sensitive and accept only these canonical values:

- `OpenLineOps:Plugins:EventLog:Provider`: `Sqlite`.
- `OpenLineOps:Plugins:ExternalHost:ExecutablePath`: the deployed
  `OpenLineOps.PluginHost` executable.
- `OpenLineOps:Plugins:ExternalHost:ArgumentsTemplate`:
  `--openlineops-plugin-host --manifest "{ManifestPath}" --entry "{EntryAssemblyPath}" --type "{EntryType}"`.
- `OpenLineOps:Plugins:ExternalHost:Sandbox:IsolationMode`: `ExternalProcess`, `LeastPrivilegeIdentity`, or `Container`.

Use `ContainerRuntimeExecutable` to select `docker`, `podman`, or another compatible
runtime; executable names are not isolation-mode aliases. Invalid or empty configured
tokens fail during service registration. Provider trials and production execution
always load third-party assemblies in `OpenLineOps.PluginHost`, never in the API
process. A headless API deployment must ship the Plugin Host beside the API or
configure an absolute path; a missing host fails closed. Process lifecycle and
event identities include Project id, Application id, and exact package hash.

The Extensions workbench runs a provider protocol trial from a validated,
unsaved resource definition. The definition is canonicalized and hashed in
memory, then sent to the same isolated Plugin Host boundary used by a persisted
resource. A trial therefore creates no temporary Application file and has no
cleanup race with published or invalid Flows.

Production deployments should prefer external process, container, or least-privilege
isolation for third-party plugins.

## Packaging Layout

Import ZIP layout:

```text
zip-root/
  manifest.json
  lib/
    OpenLineOps.SamplePlugins.LoopbackDevice.dll
    dependency.dll
  README.md
```

The manifest should match the assembly and entry type exposed by the plugin class.
Every plugin package must use the exact root `manifest.json` file name. Nested or
case-aliased manifests, path traversal, links, and case-ambiguous files are
rejected. Only explicit Studio import adds the package to `.oloapp`.

## Samples

See `samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice` for a device
command and `samples/plugins/OpenLineOps.SamplePlugins.QualityGate` for a
production process command.

Build it with:

```powershell
dotnet build samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice/OpenLineOps.SamplePlugins.LoopbackDevice.csproj
dotnet build samples/plugins/OpenLineOps.SamplePlugins.QualityGate/OpenLineOps.SamplePlugins.QualityGate.csproj
```

The loopback sample implements an `Echo` command. The quality-gate sample keeps
execution and product judgement separate: both Passed and Failed product results
return a completed command, while malformed protocol input returns an execution
failure. Together they demonstrate manifest declaration, validation-friendly
command descriptors, cancellation handling, and terminal result outcomes.

## Review Checklist

- Manifest id, version, kind, entry assembly, entry type, and capabilities are stable.
- Command ids and capability ids are documented.
- Payload schemas are versioned when external callers depend on them.
- Initialization does not connect to devices unless the plugin is intentionally activated.
- Timeouts and cancellation tokens are respected.
- Vendor SDK exceptions are translated into plugin result outcomes.
- No vendor types appear in OpenLineOps domain or application contracts.
- The distributable ZIP contains the entry assembly and every runtime dependency.
- The package passes Studio import, hash preview, validation, provider trial, and publication.
