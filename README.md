# OpenLineOps

OpenLineOps is a next-generation automation project workspace and extensible runtime platform for automated test production lines.

It is designed to let users create and open automation projects, compose applications, automation topology, equipment nodes, modules, capability contracts, driver bindings, slot groups, and slots, edit a visual site layout, author execution flows with Blockly, run flexible PythonScript logic through the .NET runtime boundary, monitor production runs, and trace results. OpenLineOps is a ground-up original open-source platform.

## Current Status

This project is in early platform development.

- Backend: .NET 10 modular monolith with DDD boundaries.
- DDD foundation: local `lib/NetDevPack` integrated through OpenLineOps domain abstractions while preserving strong typed aggregate IDs, with shared EF Core data foundations, integration-event DTO conversion, CAP-backed EventBus publishing, a compileable bounded-context living template, a production Devices EF adapter, and a repo-local modular DDD bounded-context scaffolder in place.
- Desktop: Electron, React, TypeScript, Vite, and SignalR, with a VS-inspired
  New/Open/Recent Start Center before a project is opened and an IDE workbench
  scoped to the active project and application after open.
- Persistence: in-memory and SQLite for local development, PostgreSQL adapters for deployment mode, and reusable `OpenLineOps.Infrastructure.Data.Core` package adoption through the `OpenLineOps.SampleInspection` template and Devices `EfSqlite` provider.
- Runtime: immutable Project Snapshot launch verifies release manifest schema v3,
  canonical frozen Flow IR, release-scoped process/configuration source, and
  release-scoped device routing. Dynamically expanded Python/Blockly automation
  actions are persisted as child Runtime steps and commands: steps record action,
  parent, and sequence identity, commands share the action identity, and failure,
  cancellation, and timeout propagate to the container.
- Blockly and Python: official Blockly editing, built-in, plugin-generated, and
  persisted/versioned custom blocks, Python preview/manual editing, source hashing,
  process-isolated execution, and governed automation-plan dispatch are in place.
  Runtime Action Contract v1 now provides a canonical, hashed declarative contract
  foundation for the five built-in blocks; the server-side Blockly workspace
  compiler and exact block/contract/plugin dependency locks remain future work.
- Project workspace: project-folder source is isolated by explicit project and
  application scope across topology, layout, processes, Blockly/Python artifacts,
  blocks, and Engineering configuration. A root `<projectId>.oloproj` composes
  independently movable application directories, each with its own `.oloapp` and
  application-local resources. Files under an application root retain
  `ApplicationId` but do not persist the host `ProjectId`, so the directory can
  be copied under another project's `applications` directory and imported
  without rewriting its contents. Only exact current project and Application
  resource schemas are accepted; obsolete formats have no compatibility or
  migration path. Server-side publication creates an immutable
  release containing frozen source, resolved metadata, per-file hashes, a content
  digest, and canonical Flow IR.
- Headless execution: `OpenLineOps.Runner` implements one-shot execution of an
  existing immutable Project Snapshot without opening Studio. It is not yet a
  service, queue, recovery host, package/deployment tool, or signed-package verifier.
- Open source packaging: initial documentation, contribution workflow, CI workflow, CI workflow action reference verification, CI release artifact bundle inspection evidence, sample plugin, bounded-context living template, module scaffolding command, release manifest tooling, artifact kind gates, local release staging script, third-party notice generation, release dependency inventory metadata, release metadata checksums, release provenance metadata, release candidate inspection verification, publication evidence reporting and verification, final publication preflight, unsigned desktop unpacked package staging, optional desktop signing pipeline, manifest/checksum verification, publication readiness gate with strict signed-desktop enforcement, publication metadata finalization script, and CI release artifact upload.

## Repository Map

- `src/OpenLineOps.Api`: ASP.NET Core host, health checks, OpenAPI, controllers, SignalR.
- `src/OpenLineOps.PluginHost`: external plugin host process entry point.
- `src/OpenLineOps.ScriptWorker`: process-isolated Python script worker entry point.
- `src/OpenLineOps.Runner`: one-shot headless immutable Project Snapshot runner.
- `shared`: cross-cutting contracts, DDD/application abstractions, shared infrastructure foundations, and CAP-backed EventBus integration with optional EF Core transaction coordination.
- `modules`: bounded contexts for projects, topology, processes, engineering,
  runtime, devices, operations, traceability, and plugins.
- `apps/desktop`: Electron desktop application.
- `lib/pythonscript`: in-repository Python scripting component used for Python script validation and execution integration.
- `lib/NetDevPack`: local DDD/CQRS/specification foundation referenced by shared domain and infrastructure abstractions.
- `samples/bounded-contexts`: compileable DDD module templates for new bounded contexts.
- `samples/plugins`: sample plugin projects.
- `tools/OpenLineOps.BoundedContext.Scaffolder`: .NET 10 command for generating new modular DDD/Data.Core bounded contexts.
- `tests`: unit, host-level, and integration tests.
- `docs`: architecture records, execution plan, authoring guides, and release notes.

## Prerequisites

- .NET SDK `10.0.301` or compatible .NET 10 SDK.
- Python 3 with a pythonnet-compatible shared library for Python script validation. If auto-discovery fails, set `PYTHONNET_PYDLL` to the full Python DLL path.
- Node.js 22 or newer for `apps/desktop`.
- npm 10 or newer.
- Optional Docker or Podman for PostgreSQL integration profiles, plugin sandbox tests, and containerized `OpenLineOps.ScriptWorker` execution.

## Quick Start

From the repository root:

```powershell
dotnet restore OpenLineOps.sln
dotnet build OpenLineOps.sln --no-restore
dotnet test OpenLineOps.sln --no-build
```

Run the local API:

```powershell
dotnet run --project src/OpenLineOps.Api/OpenLineOps.Api.csproj --urls http://localhost:5135
```

Run the desktop shell:

```powershell
Set-Location apps/desktop
npm install
npm run dev
```

Run an already-published immutable Project Snapshot without opening Studio:

```powershell
dotnet run --project src/OpenLineOps.Runner/OpenLineOps.Runner.csproj -- `
  run C:\Projects\LineA --snapshot active `
  --serial SN-001 --batch BATCH-001 --actor operator-a
```

Runner writes one JSON result using Runner output schema v1 to standard output
and returns a stable exit code. It accepts a project directory or
`<projectId>.oloproj` path. It rejects every other project format and refuses
draft-only projects or snapshots without an immutable release.
See `docs/automation-ide-product-shell.md` for the current codes and limitations.

The production runtime-start path is the Project Snapshot endpoint. The
development-only simulated and direct process-definition start endpoints return `403` unless both
the host environment is `Development` or `Test` and
`OpenLineOps:Runtime:DevelopmentStarts:Enabled=true` is explicitly configured.

If Electron binary download is blocked in your network, retry with:

```powershell
$env:ELECTRON_MIRROR = "https://npmmirror.com/mirrors/electron/"
npm install
```

## Verification

Default verification from the repository root:

```powershell
dotnet format OpenLineOps.sln whitespace --no-restore --verify-no-changes --exclude lib/pythonscript --exclude lib/NetDevPack --verbosity minimal
dotnet format OpenLineOps.sln style --no-restore --verify-no-changes --exclude lib/pythonscript --exclude lib/NetDevPack --severity warn --verbosity minimal
dotnet build OpenLineOps.sln --no-restore
dotnet test OpenLineOps.sln --no-build
dotnet build lib/pythonscript/PythonScript.sln --no-restore
dotnet list OpenLineOps.sln package --vulnerable --include-transitive
dotnet test tests/OpenLineOps.SampleInspection.Tests/OpenLineOps.SampleInspection.Tests.csproj --no-restore
dotnet run --project tools/OpenLineOps.BoundedContext.Scaffolder/OpenLineOps.BoundedContext.Scaffolder.csproj -- --help
dotnet run --project tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj -- --help
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-ci-workflow-actions.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-open-source-metadata.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-third-party-license-metadata.ps1
dotnet build samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice/OpenLineOps.SamplePlugins.LoopbackDevice.csproj
```

After dependency changes, regenerate the third-party notice before running the
default metadata verification:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-third-party-license-metadata.ps1 -UpdateNotice
```

Desktop verification:

```powershell
Set-Location apps/desktop
npm ci
npm run typecheck
npm run build
npm run package:win:ci
Set-Location ..\..
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-release-artifact-gate.ps1 -Configuration Debug -Version 0.0.0-local
powershell -NoProfile -ExecutionPolicy Bypass -File eng/stage-release-artifacts.ps1 -Configuration Release -Version 0.0.0-local -NoRestore -SkipDesktopBuild
dotnet run --project tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj -- --verify --artifacts artifacts/release --manifest artifacts/release/release-manifest.json --checksums artifacts/release/checksums.sha256 --require-kind source --require-kind api --require-kind desktop --require-kind plugin-host --require-kind script-worker --require-kind sample-plugin
powershell -NoProfile -ExecutionPolicy Bypass -File eng/inspect-release-candidate.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-release-candidate-inspection.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-desktop-signing-readiness.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-publication-metadata-finalization.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-publication-readiness.ps1 -AllowPendingExternal
powershell -NoProfile -ExecutionPolicy Bypass -File eng/write-publication-evidence.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-publication-evidence.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-final-publication-preflight.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/inspect-ci-release-artifact.ps1
Set-Location apps/desktop
npm run smoke:e2e
npm audit --audit-level=high --registry=https://registry.npmjs.org
```

## Architecture Rules

- Domain projects do not reference ASP.NET Core, Electron, database SDKs, or device SDKs.
- Domain abstractions align with `lib/NetDevPack` contracts while keeping OpenLineOps strong typed IDs instead of adopting NetDevPack's `Guid Id` entity base directly.
- Integration events are marked in domain events, converted to pure `Domain.Shared` DTOs through registered converters, and published through the shared CAP-backed EventBus adapter. Local development defaults to in-memory CAP transport/storage; deployment can use PostgreSQL CAP storage and RabbitMQ transport through `OpenLineOps:EventBus`. EF Core/CAP same-transaction coordination is opt-in and should only be enabled when the bounded-context database and CAP storage share the same relational transaction.
- Strong typed domain identifiers are intentional architecture, not a compatibility workaround. EF-backed adapters should use the shared Data.Core conversion helpers instead of local ad hoc ID conversion.
- Application projects own use-case orchestration and depend on ports.
- Infrastructure projects implement persistence, plugin loading, external processes, storage, and vendor integration.
- API projects expose HTTP and SignalR contracts and should not contain domain decisions.
- Electron never reads backend databases directly. It talks to the backend through HTTP, SignalR, and explicit preload APIs.
- Plugins must declare manifests and capabilities and must pass compatibility validation before activation.
- Python scripting uses the in-repository `lib/pythonscript` component; Blockly is the default desktop editing mode, plugin command manifests can generate read-only Blockly blocks, persisted custom blocks generate user-defined Python templates with desktop version browsing and restore controls, and manual Python code editing remains available. Built-in blocks also expose canonical Runtime Action Contract v1 metadata, but current releases still compile Blockly-authored Python nodes through frozen Flow IR dynamic-action slots rather than through a complete server-side Blockly contract compiler.

## Documentation

- Development plan: `docs/development-execution-plan.md`
- Automation project workspace: `docs/automation-project-workspace.md`
- Automation IDE and headless Runner product shell: `docs/automation-ide-product-shell.md`
- Composable automation model: `docs/composable-automation-model.md`
- Composable building block architecture: `docs/composable-building-block-architecture.md`
- ADR index: `docs/adr/README.md`
- Plugin authoring: `docs/plugin-authoring.md`
- Bounded-context scaffolding: `docs/bounded-context-scaffolding.md`
- Devices persistence: `docs/devices-persistence.md`
- EventBus transaction coordination: `docs/eventbus-transaction-coordination.md`
- Operations PostgreSQL deployment: `docs/operations-postgresql-deployment.md`
- Python scripting integration: `docs/python-scripting-integration.md`
- Release packaging: `docs/release-packaging.md`
- Third-party notices: `THIRD-PARTY-NOTICES.md`
- Desktop app notes: `apps/desktop/README.md`

## License

OpenLineOps is licensed under the MIT License. See `LICENSE`.
