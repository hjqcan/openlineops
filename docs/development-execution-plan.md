# OpenLineOps Development Execution Plan

Last updated: 2026-07-09

## Product Positioning

OpenLineOps is positioned as a next-generation extensible automation project workspace and runtime platform for automated test production lines. It should let users create or open automation projects, compose applications, automation topology, equipment nodes, modules, capability contracts, driver bindings, slot groups, and slots, edit a visual site layout, author execution flows with Blockly, execute flexible PythonScript logic through the .NET runtime boundary, monitor runs, and trace results.

The product should feel closer to a project-oriented desktop workbench than to a single sequence editor. The primary user asset is an automation project. Engineering configuration, device integration, process orchestration, runtime monitoring, traceability, and plugin delivery all serve that project lifecycle.

OpenLineOps is a ground-up product and platform implementation. Public design, code, documentation, and release artifacts should describe OpenLineOps as an original open-source project.

## Target Architecture

The backend uses .NET 10 and follows DDD with a modular monolith first. Electron owns the desktop shell and user experience. The backend exposes local and deployable APIs through ASP.NET Core.

Primary architecture rules:

- Domain logic lives inside bounded contexts and does not depend on ASP.NET Core, database SDKs, Electron, or device SDKs.
- DDD base abstractions are aligned with the local `lib/NetDevPack` library and OpenLineOps modular DDD conventions: aggregate roots satisfy NetDevPack aggregate contracts, repositories expose Unit of Work semantics, and integration events use interface or attribute metadata plus registered domain-event-to-DTO converters and a CAP-backed publisher adapter.
- OpenLineOps keeps strong typed IDs for aggregate identity instead of adopting NetDevPack's `Guid Id` entity base directly; this preserves bounded-context contracts while still aligning the lower-level DDD contracts.
- Application services orchestrate use cases and depend on domain abstractions, repositories, clocks, identity, and integration ports.
- Infrastructure implements persistence, device adapters, plugin loading, background workers, message buses, and external integrations.
- API projects expose HTTP, SignalR, and operational endpoints only. They should not contain domain decisions.
- Electron communicates with the backend through stable contracts, not direct database access.
- Plugins are loaded through explicit manifests, capability declarations, version checks, and sandboxed lifecycle boundaries.

## Repository Layout

Current target layout:

- `src/OpenLineOps.Api`: ASP.NET Core host, controllers, health checks, OpenAPI, realtime endpoints later.
- `src/OpenLineOps.ScriptWorker`: process-isolated Python script worker process.
- `shared/OpenLineOps.Domain.Abstractions`: base DDD abstractions such as entities, aggregate roots, value objects, and domain events.
- `shared/OpenLineOps.Application.Abstractions`: application-layer primitives such as results, paging, time, and current-user context.
- `shared/OpenLineOps.EventBus`: CAP-backed integration-event publisher, EventBus options, dependency injection for local in-memory or deployment PostgreSQL/RabbitMQ profiles, and optional EF Core transaction coordination.
- `shared/OpenLineOps.Plugin.Abstractions`: plugin manifest and lifecycle contracts.
- `shared/OpenLineOps.StateMachine.Core`: runtime state-machine contracts.
- `shared/OpenLineOps.Runtime.Contracts`: API-facing runtime DTOs and shared contracts.
- `modules`: bounded-context modules.
- `plugins`: future first-party plugin packages.
- `apps/desktop`: Electron desktop shell.
- `lib/pythonscript`: in-repository Python scripting component for Python validation/execution integration.
- `lib/NetDevPack`: local DDD/CQRS/specification foundation referenced by OpenLineOps domain abstractions.
- `tests`: unit, integration, contract, and host-level tests.

## Bounded Contexts

### Automation Project Workspace

Purpose: provide the user-facing project shell for creating, opening, composing, publishing, and running automation projects.

Initial aggregates and models:

- `AutomationProject`: root project asset with manifest, settings, draft status, and publication history.
- `ProjectApplication`: logical application or automation scenario inside a project.
- `AutomationTopology`: structural model of equipment nodes, modules, ports, connections, slots, groups, and bindings.
- `EquipmentNode`: addressable site, area, line, cell, station, unit, fixture, module, buffer, transport, device mount, external system, or logical subsystem node.
- `AutomationModule`: reusable behavior-bearing component attached to a topology node.
- `CapabilityContract`: versioned command/function contract exposed by simulator, plugin, device, process, or external service providers.
- `DriverBinding`: project-local binding between a required capability and a concrete provider.
- `SlotDefinition`: stable material endpoint for DUT, carrier, nest, fixture position, tray position, or logical work item.
- `SlotGroup`: controlled material grouping such as fixture nest, tester bank, tray row, buffer lane, or robot pick group.
- `SiteLayout`: top-down visual layout model with future 3D extension points.
- `PublishedProjectSnapshot`: immutable handoff artifact for runtime launch.

Main use cases:

- Create, open, list, rename, archive, and package automation projects.
- Compose applications, topology nodes, modules, capability contracts, driver bindings, slot groups, and slots inside a project.
- Edit the site layout with stable references to equipment nodes, modules, slot groups, slots, connections, and zones.
- Bind project capability requirements to device instances, plugin commands, simulator routes, process command providers, or external systems.
- Associate process definitions and Blockly/PythonScript flows with selected project targets.
- Publish an immutable project snapshot that freezes layout, bindings, process versions, block versions, generated Python source hashes, and engineering configuration.
- Start runtime sessions from a published project snapshot.

Acceptance criteria:

- Electron has a project-first entry experience with new project, open project, and recent projects.
- A runtime session can be traced back to project id, project snapshot id, application, equipment node, module, slot group, slot, process version, capability binding, and operator/system identity.
- Draft project edits cannot change an already published runtime snapshot.
- The first layout implementation is two-dimensional but stores semantics in a way that does not block future 3D rendering.
- Blockly remains the default process authoring mode; manual Python code is available for advanced editing.

### Workspace And Project Configuration

Purpose: manage production-line workspaces, stations, project versions, recipes, and engineering parameters.

Initial aggregates:

- `Workspace`: logical container for factories, lines, stations, and teams.
- `EngineeringProject`: versioned engineering configuration.
- `Recipe`: executable parameter set bound to product/test requirements.
- `StationProfile`: station capabilities, installed devices, and allowed flows.

Main use cases:

- Create and version an engineering project.
- Compare recipe versions.
- Publish a recipe to selected stations.
- Lock a configuration snapshot for production execution.

Acceptance criteria:

- Every runtime session references an immutable configuration snapshot.
- Configuration changes are auditable.
- Published configuration can be rolled back.

### Device Integration

Purpose: provide a stable abstraction over instruments, fixtures, PLCs, cameras, serial devices, TCP devices, and vendor SDKs.

Initial aggregates and services:

- `DeviceDefinition`: declared device type and required capabilities.
- `DeviceInstance`: physical or logical device bound to a station.
- `DeviceConnection`: connection state, endpoint, and protocol metadata.
- `DeviceCommand`: command definition, input schema, output schema, timeout, retry policy.

Main use cases:

- Register a device definition.
- Bind a device instance to a station.
- Connect, disconnect, reset, and health-check a device.
- Execute command through a plugin adapter.

Acceptance criteria:

- Device plugins cannot leak vendor-specific types into domain or application contracts.
- Command execution produces traceable command records.
- Connection state changes are observable in runtime monitoring.

### Process Orchestration

Purpose: define, validate, version, and execute test flows.

Initial aggregates:

- `ProcessDefinition`: versioned flow graph.
- `ProcessNode`: executable node such as command, decision, delay, script, measurement, or human confirmation.
- `ProcessTransition`: conditional transition between nodes.
- `ProcessValidationReport`: graph validation result.

Main use cases:

- Create a flow graph.
- Validate graph structure and node compatibility.
- Publish a process version.
- Start a runtime session from a process definition and published engineering configuration snapshot.
- Author Python script nodes through Blockly by default, with manual Python code editing available.

Acceptance criteria:

- Invalid graphs cannot be published.
- Process definitions are immutable after publication.
- Runtime execution can be replayed from persisted events and snapshots.
- Python script nodes use `lib/pythonscript`; OpenLineOps must not introduce a second Python runtime wrapper.
- Published Python script nodes preserve both Blockly workspace data and Python source snapshots.

### Runtime Execution

Purpose: coordinate execution sessions, state transitions, commands, retries, cancellation, and failure handling.

Initial aggregates:

- `RuntimeSession`: one execution instance for a station and process version.
- `RuntimeStep`: one executed node instance.
- `RuntimeCommand`: command sent to device/plugin/worker.
- `RuntimeIncident`: failure, timeout, manual intervention, or safety stop.

Main use cases:

- Start, pause, resume, stop, and cancel a runtime session.
- Execute process nodes deterministically.
- Persist state transitions and command results.
- Recover interrupted sessions after process restart.

Acceptance criteria:

- Session state transitions are explicit and tested.
- Every command has a terminal status.
- Cancellation and stop commands are idempotent.
- Runtime can emit progress events for UI monitoring.
- Python script execution goes through an application port and infrastructure adapter, not through domain objects.

### Monitoring And Operations

Purpose: expose live runtime state, logs, metrics, station health, alarms, and operator actions.

Initial models:

- `StationStatus`
- `RuntimeTimeline`
- `Alarm`
- `OperatorAction`
- `OperationalMetric`

Main use cases:

- View live station dashboard.
- Subscribe to runtime progress through SignalR.
- Acknowledge alarms.
- Inspect current and recent sessions.

Acceptance criteria:

- UI can render current state without polling critical endpoints aggressively.
- Monitoring data distinguishes live transient state from persisted trace records.
- Operator actions are authenticated and audited.

### Data Traceability

Purpose: persist production results, measurements, command records, artifacts, and audit trails.

Initial aggregates:

- `TraceRecord`
- `MeasurementRecord`
- `ArtifactRecord`
- `AuditEntry`
- `ResultJudgement`

Main use cases:

- Query session trace by serial number, batch, station, fixture, and time range.
- Export trace package.
- Attach artifacts such as images, logs, CSV, or binary vendor output.
- Generate result judgement from configured rules.

Acceptance criteria:

- Every result is linked to process version, recipe version, station, device, and operator/system identity.
- Trace queries support pagination and stable ordering.
- Artifact storage is abstracted from local file system or object storage choice.

### Plugin Delivery

Purpose: package and load device drivers, process nodes, integrations, reports, and UI extensions.

Initial concepts:

- `PluginManifest`
- `PluginPackage`
- `PluginCapability`
- `PluginRuntimeContext`
- `PluginHealth`

Main use cases:

- Install, enable, disable, and remove plugins.
- Validate plugin compatibility.
- Load first-party and third-party plugins through a controlled lifecycle.
- Expose plugin capabilities to process design and runtime execution.

Acceptance criteria:

- A plugin must declare id, name, version, kind, entry assembly, entry type, and capabilities.
- Platform rejects incompatible plugin versions.
- Plugin initialization failure degrades only the dependent capability, not the whole platform unless configured.

## Implementation Milestones

### Milestone 0: Foundation Baseline

Status: completed for the current planned scope.

Delivered on 2026-06-29:

- .NET 10 SDK lock through `global.json`.
- Solution and project skeleton.
- Central package management through `Directory.Packages.props`.
- Shared DDD abstractions.
- Application result and paging abstractions.
- Plugin manifest and lifecycle abstractions.
- Runtime contract primitives.
- ASP.NET Core API host with platform and health endpoints.
- xUnit test projects for domain abstractions and API host.
- Architecture Decision Records index and template under `docs/adr`.
- Accepted ADRs for modular monolith, DDD layering, Electron/backend boundary, plugin contract lifecycle, and API versioning/OpenAPI grouping.
- Shared API metadata abstractions for route constants, bounded-context OpenAPI groups, and current API version.
- Repository-wide `.editorconfig` for deterministic LF line endings, indentation, C# formatting, and analyzer severity policy.
- `Directory.Build.props` enables code style enforcement during build.
- Optional `tests/OpenLineOps.PostgresIntegration.Tests` project for Docker-backed PostgreSQL persistence integration coverage outside the default solution test path.
- Verified restore, build, and tests.

Next tasks:

- Add CI workflow once the GitHub repository exists.
- Add module template for bounded contexts.

Exit criteria:

- `dotnet restore OpenLineOps.sln` succeeds.
- `dotnet build OpenLineOps.sln --no-restore` succeeds with zero warnings.
- `dotnet test OpenLineOps.sln --no-build` succeeds.

### Milestone 1: Runtime Core

Goal: implement the execution kernel before building UI-heavy features.

Status: in progress.

Deliverables:

- Runtime session aggregate.
- Runtime state machine.
- Runtime command lifecycle.
- Pause, resume, stop, cancel, and failure transitions.
- Runtime event stream abstraction.
- In-memory runtime runner for tests.
- Persistence ports for sessions, steps, commands, and incidents.

Delivered on 2026-06-29:

- `modules/OpenLineOps.Runtime.Domain` bounded-context domain project.
- `tests/OpenLineOps.Runtime.Tests` behavior test project.
- Runtime session aggregate with immutable station, process version, configuration snapshot, and recipe snapshot inputs.
- Runtime session statuses and explicit transition methods.
- Pause, resume, stop, cancel, complete, and fail lifecycle handling.
- Idempotent repeated pause, resume, stop, and cancel handling.
- Runtime step entity and lifecycle.
- Runtime command entity and lifecycle.
- Runtime incident entity.
- Domain events for session, step, command, and incident changes.
- Unit tests for valid transitions, invalid transitions, idempotency, command timeout, terminal command states, and command event order.
- Runtime Domain dependency direction kept inward: it depends on domain abstractions, not API-facing runtime contracts.
- `modules/OpenLineOps.Runtime.Application` application-layer project.
- Application ports for session persistence, command execution, domain-event publishing, and runtime id generation.
- Executable runtime process and node models for the first fake process runner.
- `RuntimeSessionRunner` application service that creates a session, executes process nodes, updates command/step/session state, persists sessions, publishes domain events, and stops on terminal failures.
- Deterministic runner tests proving a fake two-node process can complete without real devices.
- Runner tests proving command failure marks the session failed, records an incident, and stops remaining nodes.
- Validation test proving empty processes are rejected before persistence or command execution.
- `modules/OpenLineOps.Runtime.Infrastructure` infrastructure adapter project.
- In-memory runtime session repository implementing save, lookup, and recoverable-session listing.
- In-memory runtime domain-event publisher for local development and tests.
- Runtime recovery service that builds a stable recovery plan from non-terminal persisted sessions.
- Recovery tests proving terminal sessions are ignored and running/paused sessions are returned with recovery reasons.
- SQLite runtime session repository added behind the existing `IRuntimeSessionRepository` application port.
- Runtime sessions are persisted as aggregate JSON snapshots with relational columns for session id, status, station, process, configuration snapshot, recipe snapshot, and last transition time.
- Runtime domain entities now expose controlled restore factories so infrastructure can rehydrate sessions, steps, commands, and incidents without reflection or ORM field mapping.
- Runtime module dependency injection can select `InMemory` or `Sqlite` persistence through `OpenLineOps:Runtime:Persistence`.
- Repository tests prove a session graph survives a repository restart, recovery plans are built from SQLite-backed non-terminal sessions, and missing sessions return null.
- PostgreSQL runtime session repository added behind the same `IRuntimeSessionRepository` application port for deployment-mode persistence.
- PostgreSQL runtime sessions use the same aggregate JSON snapshot shape as SQLite, stored in a `jsonb` document column with relational query columns for session id, status, station, process, configuration snapshot, recipe snapshot, and last transition time.
- Runtime module dependency injection can select `PostgreSql` persistence through `OpenLineOps:Runtime:Persistence`, with `Postgres` and `PostgreSQL` accepted as aliases.
- Configuration tests cover required PostgreSQL connection strings and prove repository construction does not open a database connection.
- Optional Testcontainers-backed PostgreSQL integration test proves a runtime session graph can be saved, reloaded through a new repository instance, and included in recovery planning when `OPENLINEOPS_RUN_POSTGRES_INTEGRATION=1` is set.
- `modules/OpenLineOps.Runtime.Api` API module project.
- Runtime controller registration through the ASP.NET Core host.
- Runtime dependency injection registration for runner, repository, domain-event publisher, id provider, clock, and simulated command executor.
- HTTP endpoints for starting simulated runtime sessions, querying runtime sessions by id, and reading the recovery plan.
- Host-level integration tests covering simulated session start/query, failed simulated sessions, recovery-plan response, and invalid request validation.

Suggested projects:

- `modules/OpenLineOps.Runtime.Domain`
- `modules/OpenLineOps.Runtime.Application`
- `modules/OpenLineOps.Runtime.Infrastructure`
- `modules/OpenLineOps.Runtime.Api`
- `tests/OpenLineOps.Runtime.Tests`

Key tests:

- Valid state transitions.
- Invalid state transition rejection.
- Idempotent stop/cancel.
- Command timeout.
- Session event ordering.
- Fake process execution to completed session.
- Runtime recovery from persisted state.
- Runtime API start/query/recovery integration tests.

Exit criteria:

- A test can start a fake process, execute fake command nodes, and complete a session.
- Runtime session emits deterministic events.
- No real device dependency is required.

### Milestone 2: Process Definition And Orchestration

Goal: model versioned test flows and validate executable process graphs.

Deliverables:

- Process definition aggregate.
- Node and transition model.
- Graph validation service.
- Process publication workflow.
- Process definition REST API.
- Runtime bridge from published process to executable session.

Delivered on 2026-06-29 (domain slice):

- `modules/OpenLineOps.Processes.Domain` bounded-context domain project.
- `tests/OpenLineOps.Processes.Tests` behavior test project.
- Process definition aggregate with draft and published lifecycle.
- Versioned process identity, process node identity, transition identity, and required capability identity value objects.
- Node model for start, command, decision, delay, and end nodes.
- Transition model connecting source and target process nodes.
- Process publication workflow that validates graph structure before publishing.
- Published process definitions are immutable through the aggregate API.
- Domain event emitted when a process definition is published.
- Command nodes carry runtime execution metadata: required capability, command name, timeout, and optional input payload.
- Graph validator for empty graphs, exact start-node cardinality, missing transition endpoints, unreachable nodes, command nodes without required capabilities, missing command names, invalid command timeouts, and cycles without explicit loop policy.
- Tests for successful publication, invalid publication rejection, missing start node, unreachable node, missing transition target, cycle detection, immutable published definitions, duplicate node rejection, command capability validation, and command execution metadata validation.

Delivered on 2026-06-29 (application and API slice):

- `modules/OpenLineOps.Processes.Application` application-layer project.
- `modules/OpenLineOps.Processes.Infrastructure` infrastructure adapter project.
- `modules/OpenLineOps.Processes.Api` ASP.NET Core API module project.
- Process definition repository port for aggregate persistence.
- Process definition application service for create, list, query, validate, and publish use cases.
- Application DTOs for process definitions, nodes, transitions, summaries, and validation reports.
- In-memory process definition repository for local development and integration tests.
- Process module dependency injection registration for repository, clock, and application service.
- Process API controller mounted into the host through `AddApplicationPart`.
- HTTP endpoints for creating process definitions, listing definitions, querying by id, validating graph structure, and publishing definitions.
- Host-level integration tests covering create/query, invalid graph validation, successful publish, invalid publish rejection, duplicate create conflict, and missing definition lookup.

Delivered on 2026-06-29 (runtime bridge slice):

- Process runtime launcher application service that loads a published process definition and starts a runtime session.
- Runtime bridge from a published, validated, linear process graph to `ExecutableRuntimeProcess`.
- Command-node mapping from process node execution metadata to runtime node capability, command name, timeout, and input payload.
- HTTP endpoint for starting a runtime session from a published process definition.
- End-to-end integration test proving create -> publish -> start runtime session -> query runtime session.
- Conflict handling that prevents draft process definitions from starting runtime sessions.

Delivered on 2026-06-29 (engineering configuration binding slice):

- Process runtime start request now accepts a published engineering `configurationSnapshotId` instead of loose station and recipe inputs.
- `IRuntimeConfigurationSnapshotResolver` application port added to keep Processes decoupled from Engineering domain internals.
- Engineering infrastructure adapter resolves published configuration snapshots into Runtime launch inputs.
- Runtime session aggregate now captures immutable `ConfigurationSnapshotId` in addition to station, process version, and recipe snapshot references.
- Runtime API query, simulated run response, and recovery-plan models expose configuration snapshot id for traceability.
- Process runtime launch validates that the configuration snapshot belongs to the requested process definition and exact process version.
- Host-level integration tests cover create process -> publish process -> publish engineering snapshot -> start runtime -> query runtime with configuration snapshot traceability.
- Conflict handling rejects a configuration snapshot that belongs to another process definition.

Delivered on 2026-06-29 (persistence foundation slice):

- SQLite process definition repository added behind the existing `IProcessDefinitionRepository` application port.
- Process definitions are persisted as versioned aggregate JSON snapshots with query columns for id, status, and update time.
- SQLite schema creation is local to the infrastructure adapter and runs lazily on first repository use.
- Process module dependency injection can select `InMemory` or `Sqlite` persistence through `OpenLineOps:Processes:Persistence`.
- Default host configuration remains `InMemory` to preserve lightweight local startup and existing API tests.
- Repository tests prove a published process definition survives a repository restart, preserves command execution metadata, returns ordered lists, and returns null for missing definitions.
- PostgreSQL process definition repository added behind the same application port for deployment-mode persistence.
- PostgreSQL process definitions use the same aggregate JSON snapshot shape as SQLite, stored in a `jsonb` document column with relational query columns for id, status, and update time.
- Process module dependency injection can select `PostgreSql` persistence through `OpenLineOps:Processes:Persistence`, with `Postgres` and `PostgreSQL` accepted as aliases.
- Configuration tests cover required PostgreSQL connection strings and prove repository construction does not open a database connection.
- Optional Testcontainers-backed PostgreSQL integration test proves a published process definition can be saved, reloaded through a new repository instance, and listed from PostgreSQL when `OPENLINEOPS_RUN_POSTGRES_INTEGRATION=1` is set.

Delivered on 2026-06-30 (Python script node metadata slice):

- Added `ProcessNodeKind.PythonScript` and `ProcessScriptEditorMode` to model Python script nodes without coupling the Process domain to Python runtime, Blockly, Electron, or `pythonnet`.
- Python script nodes carry script language, editor mode, Blockly workspace JSON, Python source code, source hash, script version, script timeout, and optional input payload.
- Python script source hashes are computed from the persisted Python source for traceability.
- Application and API create/query DTOs now accept and return Python script metadata.
- API creation defaults omitted Python script editor mode to `Blockly`; `ManualCode` remains available for advanced editing.
- Graph validation blocks publication when Python script nodes lack required editor mode, Blockly workspace JSON in Blockly mode, source code, source hash, script version, or positive timeout.
- SQLite/PostgreSQL process definition snapshot mapping preserves Python script metadata and input payload with the same aggregate JSON snapshot strategy used by existing nodes.
- `lib/pythonscript/PythonScript/PythonScript.csproj` now multi-targets `net8.0;net10.0`.
- PythonScript package versions were moved into central package management through `Directory.Packages.props`.
- Added domain, repository, and host-level API tests for Python script node publishing, metadata validation, script hash generation, snapshot round-trip, default Blockly creation, invalid editor modes, and runtime launch protection.

Delivered on 2026-06-30 (Python script validation slice):

- Added `IProcessScriptDefinitionValidator` as the application-layer script definition validation port.
- Added `ProcessScriptValidationReport` and `ProcessScriptValidationIssue` so application/API behavior does not depend on PythonScript component DTOs.
- Process publication now runs graph validation and Python script validation before mutating a draft definition to published.
- Added `PythonScriptDefinitionValidator` in Processes Infrastructure backed by `PythonScript.SyntaxStaticCheck.PythonSyntaxChecker`.
- PythonScript validation fails closed when pythonnet cannot initialize the Python runtime or syntax validation throws.
- The infrastructure adapter auto-discovers the Python DLL through the local `python` CLI when `PYTHONNET_PYDLL` is not already configured.
- Process module dependency injection registers the PythonScript-backed validator by default while still allowing tests/deployments to override the application port.
- Added application tests proving script validation failures prevent publication and do not save mutated definitions.
- Added infrastructure tests proving valid Python source passes and invalid Python source returns syntax issues.
- Added host-level API test proving publishing invalid Python script source returns `Validation.Processes.PythonScriptValidationFailed`.

Delivered on 2026-06-30 (Python script runtime execution slice):

- Added Runtime application scripting contracts:
  - `RuntimeScriptCommand`
  - `RuntimeScriptCommandPayload`
  - `RuntimeScriptExecutionRequest`
  - `IRuntimeScriptExecutor`
- Added `PythonScriptRuntimeScriptExecutor` in Runtime Infrastructure backed by `PythonScript.Runtime.PythonRuntimeSession`.
- Runtime script commands use target capability `process.python-script` and command name `PythonScript.Execute`.
- The runtime script command payload carries script language, published Python source, script version, and optional node input payload.
- Runtime and Devices configurable command executors route Python script commands to `IRuntimeScriptExecutor` before simulator, device-backed, or plugin execution.
- Process runtime launcher now maps published `PythonScript` process nodes into executable runtime nodes instead of rejecting them.
- Python script execution scope exposes `input_payload`, `script_version`, `session_id`, `station_id`, `configuration_snapshot_id`, `node_id`, and `command_id`.
- If script code sets `result`, the adapter serializes it to compact JSON and stores it as the runtime command result payload.
- Python exceptions become failed runtime command results and continue through the existing Runtime command/session failure handling.
- Added Runtime tests for successful Python execution, Python exception failure, and unsupported language rejection.
- Added dependency-injection tests proving Runtime and Devices modules preserve script-command routing even when plugin or device-backed runtime command execution is configured.
- Added host-level API test proving a published PythonScript process can start a runtime session and return the script result through runtime command trace output.

Delivered on 2026-06-30 (Python script worker isolation slice):

- Added `OpenLineOps.ScriptWorker` as a .NET 10 console worker that executes one Python script request per process.
- Refactored Python runtime execution into `PythonScriptExecutionScope` so in-process execution and worker-process execution use the same `lib/pythonscript` path and script contract.
- Added `PythonScriptRuntimeOptions` under `OpenLineOps:Runtime:Scripting:Python`.
- Added execution modes:
  - `InProcessTrusted` for development or trusted first-party scripts.
  - `ProcessIsolated` for running scripts in a separate worker process.
- Added `ConfigurableRuntimeScriptExecutor` so Runtime and Devices module composition can select the Python execution mode from configuration.
- Added `ProcessIsolatedPythonScriptRuntimeScriptExecutor`, which starts the configured worker command, sends a JSON request over stdin, reads one terminal JSON result from stdout, and kills the worker process tree on runtime timeout or cancellation.
- The Python execution scope now captures Python `stdout` and `stderr` while script source runs, preventing `print()` output from corrupting the worker protocol.
- Added Runtime tests proving real process-isolated worker execution returns script results, worker-missing configuration is rejected, Python exceptions still fail commands, and unsupported languages are rejected.
- Added API dependency-injection tests proving Runtime and Devices modules can select `ProcessIsolated` mode through configuration.

Delivered on 2026-06-30 (Python script worker sandbox slice):

- Added `PythonScriptWorkerSandboxOptions` under `OpenLineOps:Runtime:Scripting:Python:Sandbox`.
- Added sandbox isolation modes for direct external process, container runtime, and least-privilege identity launcher.
- Container mode builds `docker run`/`podman run` style commands with `--rm`, `--interactive`, optional `--user`, `--network none`, `--security-opt no-new-privileges`, `--cap-drop ALL`, `--read-only`, `--pids-limit`, read-only workspace mount, container path rewriting, sandbox environment metadata, and additional configured run arguments.
- Least-privilege identity mode supports Unix-style `sudo -n -u <identity> -- env ... <worker>` and custom launcher executable/templates for platform-specific low-privilege account isolation.
- `ProcessIsolatedPythonScriptRuntimeScriptExecutor` now validates sandbox policy before launch and rejects configurations that require least-privilege execution without either an identity launcher or container isolation.
- Runtime and Devices module composition bind the sandbox options from configuration.
- Added Runtime tests for external process, container, least-privilege launcher, missing sandbox policy, and process-isolated rejection behavior; added API dependency-injection tests for Runtime and Devices sandbox option binding.

Delivered on 2026-07-09 (decision branch runtime execution slice):

- Extended `ExecutableRuntimeProcess` beyond linear node lists with `StartNodeId`, routing nodes, and labeled transitions.
- Added runtime routing node models for `Start`, `Decision`, `Delay`, and `End` without making routing metadata executable command work.
- `RuntimeSessionRunner` now traverses graph-shaped processes from the start node, executes command/PythonScript nodes, and follows transitions until `End`, terminal command failure, missing route, or hop-limit failure.
- Decision routing reads the previous successful command result payload, extracts branch values from JSON properties `decision`, `route`, `branch`, `next`, and `status`, or from a raw string payload.
- Decision transition labels match branch values case-insensitively and support a `default` or unlabeled fallback transition.
- Missing matching decision branches fail the runtime session with a traceable `Runtime.DecisionBranchNotMatched` incident instead of silently choosing a path.
- Process runtime launch now preserves published process graph shape when mapping a definition into `ExecutableRuntimeProcess`.
- Multiple outgoing runtime transitions are allowed only from `Decision` nodes; duplicate decision labels are rejected before session start.
- Added Runtime tests proving a `{"status":"ok"}` payload routes to the matching branch and that unmatched branch values fail the session with an incident.
- Added host-level API test proving create -> publish -> engineering snapshot -> start runtime -> query runtime works for a published process containing a Decision branch.

Delivered on 2026-07-09 (explicit loop policy slice):

- Added `ProcessTransitionLoopPolicy` with a `Counted` policy and optional `MaxTraversals` on process transitions.
- Process graph validation still rejects accidental cycles by default, but permits cycles only when the cycle is broken by at least one explicit counted loop transition.
- Counted loop transitions must declare a positive max traversal count, must originate from a Decision node, and must route back to a reachable earlier node.
- Process application/API DTOs now accept and return transition `loopPolicy` and `maxTraversals`.
- SQLite/PostgreSQL process-definition aggregate snapshots preserve transition loop policy fields while treating missing archived fields as `None`.
- Process runtime bridge maps counted loop policies to executable runtime transition traversal limits.
- Runtime graph execution records counted transition traversals and fails the session with `Runtime.LoopTransitionLimitExceeded` when a loop exceeds its declared limit.
- Electron process editor contracts and transition editor now expose `None`/`Counted` loop policy and max traversal editing.
- Added Process domain tests for counted loop publication and invalid loop-policy validation.
- Added Process persistence tests proving loop policy survives repository round-trip.
- Added Runtime tests proving counted loop retry execution and max traversal failure behavior.
- Added host-level API test proving a published process with an explicit counted loop policy can start a runtime session.

Remaining Milestone 2 work:

- No remaining Milestone 2 backend work is currently known for the planned scope.

Key tests:

- Reject graph with no start node.
- Reject unreachable nodes.
- Reject cycles unless loop policy is explicit.
- Reject node requiring unavailable capability.
- Published process is immutable.

Exit criteria:

- A published process can drive the Milestone 1 runtime runner.
- API can create, validate, publish, and query process definitions.

### Milestone 3: Engineering Configuration

Goal: connect process execution to stable production configuration.

Deliverables:

- Workspace, engineering project, station profile, and recipe models.
- Versioned configuration snapshots.
- Publish and rollback workflow.
- Configuration diff API.
- Runtime session binding to configuration snapshot.

Delivered on 2026-06-29 (domain slice):

- `modules/OpenLineOps.Engineering.Domain` bounded-context domain project.
- `tests/OpenLineOps.Engineering.Tests` behavior test project.
- Workspace model for grouping engineering projects.
- Engineering project aggregate with active configuration snapshot tracking.
- Recipe aggregate with draft/published lifecycle and immutable published versions.
- Station profile model with device capability bindings.
- Published configuration snapshot model containing process definition, process version, recipe, recipe version, station profile, and device binding references.
- Publish workflow that rejects draft recipes and station profiles without device bindings.
- Rollback workflow that switches an engineering project back to a previously published snapshot.
- Domain events for recipe publication, configuration snapshot publication, and engineering project rollback.
- Tests for workspace identity, recipe immutability after publish, draft recipe rejection, snapshot traceability references, rollback behavior, missing snapshot rejection, duplicate capability binding rejection, and station binding requirements.

Delivered on 2026-06-29 (application and API slice):

- `modules/OpenLineOps.Engineering.Application` application-layer project.
- `modules/OpenLineOps.Engineering.Infrastructure` infrastructure adapter project.
- `modules/OpenLineOps.Engineering.Api` ASP.NET Core API module project.
- Repository ports for engineering projects, recipes, and station profiles.
- Engineering configuration application service for creating, listing, and querying projects, recipes, and station profiles.
- Workspace lifecycle application service for creating, listing, and querying workspaces.
- Recipe publication use case that moves draft recipes into immutable published state.
- Configuration snapshot publication use case that binds project, process definition/version, recipe/version, station profile, and device bindings.
- Configuration rollback use case that switches the active project snapshot back to a previously published snapshot.
- In-memory engineering repositories for local development and host-level integration tests.
- Engineering module dependency injection registration for repositories, clock, and application service.
- Engineering API controller mounted into the host through `AddApplicationPart`.
- Bounded-context OpenAPI group and route metadata for `engineering-v1`.
- HTTP endpoints under `/api/engineering` for projects, recipes, station profiles, configuration snapshot publication, and snapshot rollback.
- HTTP endpoints under `/api/engineering/workspaces` for workspace creation, listing, and lookup.
- Engineering project creation now validates that the referenced workspace exists instead of accepting a free-form workspace id.
- Host-level integration tests covering published engineering inputs -> snapshot publication -> rollback, draft recipe snapshot rejection, duplicate station capability validation, and API metadata grouping.
- Host-level integration tests covering workspace lifecycle and missing-workspace rejection during project creation.

Delivered on 2026-06-29 (configuration diff slice):

- Configuration snapshot diff application model added for project id, source snapshot, target snapshot, and changed fields.
- Engineering configuration service can compare two published project snapshots.
- Diff coverage includes process definition/version, recipe/version, station profile, and device binding additions, removals, and field changes.
- Engineering API exposes `GET /api/engineering/projects/{projectId}/configuration-snapshots/{fromSnapshotId}/diff/{toSnapshotId}`.
- Host-level integration test covers two published snapshots with recipe, station, and device binding differences.

Delivered on 2026-06-29 (persistence foundation slice):

- SQLite engineering repositories added behind the existing project, recipe, and station profile repository ports.
- Engineering workspaces, projects, configuration snapshots, recipes, and station profiles are persisted as aggregate JSON snapshots with relational query columns for key identities and status fields.
- Engineering domain objects expose controlled restore factories so infrastructure can rehydrate published recipes, station device bindings, active project snapshots, and configuration snapshots without reflection.
- Engineering module dependency injection can select `InMemory` or `Sqlite` persistence through `OpenLineOps:Engineering:Persistence`.
- Repository tests prove engineering configuration survives repository restart, runtime snapshot resolution works against SQLite-backed projects, lists are ordered by id, and missing lookups return null.
- PostgreSQL engineering repositories now cover workspaces, projects, recipes, and station profiles for deployment-mode persistence.
- PostgreSQL engineering documents use the same aggregate JSON snapshot shapes as SQLite, stored in `jsonb` document columns with relational query columns for workspace display name, project workspace, active snapshot, recipe status, and station device binding counts.
- Engineering module dependency injection can select `PostgreSql` persistence through `OpenLineOps:Engineering:Persistence`, with `Postgres` and `PostgreSQL` accepted as aliases.
- Configuration tests cover required PostgreSQL connection strings and prove engineering repository construction does not open database connections.
- Optional Testcontainers-backed PostgreSQL integration test proves workspace, project, recipe, station profile, configuration snapshot, and runtime snapshot resolution persist across new repository instances when `OPENLINEOPS_RUN_POSTGRES_INTEGRATION=1` is set.

Remaining Milestone 3 work:

- None at the current milestone scope.

Key tests:

- Recipe version immutability after publish.
- Runtime cannot start from draft configuration.
- Snapshot contains process, recipe, station, and device binding references.

Exit criteria:

- Runtime session is fully traceable to immutable engineering inputs.

### Milestone 4: Device Abstraction And Plugin Host

Goal: execute runtime commands through device and process-node plugins.

Deliverables:

- Plugin package discovery.
- Plugin compatibility validation.
- Plugin lifecycle manager.
- Device command abstraction.
- Fake device plugin.
- First real adapter candidate, such as serial/TCP or a configuration-backed simulator.

Delivered on 2026-06-29 (domain and adapter foundation slice):

- `modules/OpenLineOps.Devices.Domain` bounded-context domain project.
- `modules/OpenLineOps.Devices.Application` application-layer project.
- `modules/OpenLineOps.Devices.Infrastructure` infrastructure adapter project.
- `tests/OpenLineOps.Devices.Tests` behavior test project.
- Device definition aggregate for plugin-owned device types.
- Device capability model and command definition model with input schema, output schema, timeout, and retry metadata.
- Device definition rules reject duplicate capabilities, duplicate command names, and commands whose capabilities have not been declared.
- Device instance aggregate for station-bound physical or logical devices.
- Device endpoint value model for protocol and address metadata.
- Device connection lifecycle states for disconnected, connecting, connected, and faulted devices.
- Device connection status domain event emitted for state changes.
- Device instance rules reject marking a device connected before connection has been requested and require fault reset before reconnecting.
- Application-layer device command execution port with execution request, result, and terminal outcomes.
- Fake device command executor infrastructure adapter for local development and tests, including completed, failed, rejected, and timed-out command outcomes.
- Unit tests cover device definition rules, command validation, connection lifecycle events, fault reset behavior, and fake command execution results.
- Runtime command execution context now carries station id and configuration snapshot id so device routing can remain traceable to production configuration.
- Application-layer device command route resolver port added to map runtime command intent to a concrete device instance and command definition.
- Device-backed runtime command executor adapter added in Devices infrastructure. It implements Runtime's `IRuntimeCommandExecutor` and delegates execution to `IDeviceCommandExecutor`.
- Static device command route resolver added as a local-development bridge until engineering snapshot and plugin manifest routing are implemented.
- Runtime-to-device bridge tests prove runtime context mapping, missing-route rejection, device terminal outcome mapping, and a runtime session completing through the fake device command boundary.
- `modules/OpenLineOps.Devices.Api` module composition project added for device runtime bridge registration.
- Engineering-backed device command route resolver added. It resolves runtime commands from published configuration snapshots, station profile device bindings, and required device capabilities to concrete device keys.
- Devices module can now select runtime command execution through configuration. Default execution remains simulator-backed; setting `OpenLineOps:Runtime:CommandExecutor` to `Device` routes runtime commands through the Devices bridge.
- Device command route provider can be selected through `OpenLineOps:Devices:CommandRouting:Provider`, with `Engineering` and `Static` supported.
- Host-level API test proves a published process and engineering snapshot can start a runtime session with device-backed execution and produce a fake device command result tied to the configured device key.
- `modules/OpenLineOps.Plugins.Application` plugin host application project added for package discovery ports, manifest compatibility validation, and lifecycle management.
- `modules/OpenLineOps.Plugins.Infrastructure` plugin host infrastructure project added with file-system manifest discovery.
- Plugin manifests now carry optional `contractVersion`, `minimumPlatformVersion`, and `deviceCommands` fields while remaining source-compatible with the earlier contract constructor.
- Plugin manifest validator rejects missing id/name/version/entry assembly/entry type, unsupported kinds, invalid versions, incompatible contract/platform versions, duplicate capabilities, empty capability declarations, invalid device command definitions, duplicate command definitions, and commands that reference undeclared capabilities.
- Plugin lifecycle manager discovers packages, validates manifests before activation, initializes valid plugins, records degraded and failed initialization states, isolates failed plugins from healthy plugins, and disposes initialized/degraded plugins during stop.
- Manifest-only plugin activator added as a local-development bootstrap adapter.
- Collectible `AssemblyLoadContext` plugin activator added for real entry assembly loading, package-directory path confinement, shared plugin-contract assembly binding, manifest id mismatch rejection, and unload on plugin disposal.
- Plugin tests cover manifest compatibility validation, file-system package discovery, invalid-manifest rejection before activation, isolated initialization failure, degraded initialization, lifecycle disposal, assembly plugin activation, entry type validation, path confinement, and manifest id mismatch rejection.
- Compatible plugin capability inventory added in Plugins Application. It lists capabilities only from manifests that pass compatibility validation and exposes capability checks for runtime/device routing.
- Engineering-backed device command routing now validates snapshot-bound capabilities against the plugin capability inventory when one is registered. If no inventory is registered, local simulator behavior remains unchanged.
- Route validation tests prove a snapshot binding is rejected when no compatible plugin declares the required capability and accepted when a compatible device-driver manifest declares it.
- Compatible plugin device command inventory added in Plugins Application. It lists concrete device commands only from compatible manifests and supports capability plus command-name lookup.
- Engineering-backed device command routing now validates snapshot-bound commands against plugin-declared device command definitions when a command inventory is registered, and uses the plugin manifest command definition id in the resolved device route.
- Route validation tests prove a snapshot binding is rejected when the plugin declares the capability but not the requested command, and accepted when the compatible device-driver manifest declares the concrete command.
- Device definition and device instance repository ports added in Devices Application.
- Devices Infrastructure now provides in-memory, handwritten SQLite, and EF/Data.Core-backed SQLite persistence adapters for device definitions and station-bound device instances. The handwritten SQLite adapter uses the same aggregate-document persistence style as runtime and engineering; the EF adapter validates the new Data.Core bounded-context path against real production aggregates.
- Devices module registers device persistence through `OpenLineOps:Devices:Persistence`, defaulting to Data.Core EF SQLite through `Provider=EfSqlite`, with explicit `Provider=InMemory` for ephemeral sessions and snapshot SQLite through `Provider=Sqlite`.
- Device persistence tests prove definition graphs, command metadata, connection state, station queries, and restored aggregates without pending domain events.
- Configuration-backed simulator device command executor added as the first non-hardcoded adapter candidate. It matches commands by command definition id, capability plus command name, or command name, and supports configured terminal outcomes, result payloads, failure reasons, and timeout-aware delay simulation.
- Devices module can select the device command execution adapter through `OpenLineOps:Devices:CommandExecution:Provider`. The default remains `Fake`; `ConfiguredSimulator`, `Config`, and `Simulator` select the configuration-backed simulator.
- Devices module configuration selection now supports both ASP.NET Host configuration overrides and standalone `ServiceCollection` usage by registering a fallback configuration source and retaining loaded options as defaults.
- Configured simulator tests cover exact command-definition matching, capability plus command-name matching, default unmatched-command rejection, configured failed/rejected/timed-out outcomes, and delay exceeding request timeout.
- External-process plugin lifecycle boundary added in Plugins Infrastructure for untrusted plugin activation. The activator returns a proxy without loading the plugin entry assembly into the host process, validates that the entry assembly stays inside the package directory, and starts the plugin host through an `IExternalPluginProcessRunner` during initialization.
- Default external process runner uses `System.Diagnostics.Process` with `UseShellExecute=false`, hidden window startup, package-directory working directory, configurable executable/arguments, manifest/package environment variables, startup probe delay, and process-tree termination on dispose.
- External-process plugin tests cover delayed process startup, sandbox path confinement, missing entry assembly rejection, already-exited process failure, and lifecycle manager start/stop through the process boundary.
- Plugin device command invocation protocol added in Plugins Application with request/result DTOs, terminal outcomes, and an `IPluginDeviceCommandInvoker` port so command execution can cross a plugin boundary without Devices depending on plugin infrastructure.
- External process plugin registry and command invoker added in Plugins Infrastructure. Lifecycle initialization registers active external processes, disposal unregisters them, and the invoker dispatches device command requests to the registered plugin process.
- Default external process runner now supports a JSON-lines command protocol over redirected stdin/stdout using `device-command` and `device-command-result` envelopes with request correlation and timeout handling.
- Devices Infrastructure now includes `PluginDeviceCommandExecutor`, which maps `DeviceCommandExecutionRequest` into plugin command invocations by using compatible plugin command inventory metadata, validates manifest command-definition identity, and maps plugin terminal outcomes back to device command outcomes.
- Devices module can select plugin-backed command execution through `OpenLineOps:Devices:CommandExecution:Provider=Plugin`, with `ExternalPlugin` and `PluginProcess` accepted as aliases.
- Tests cover plugin command invocation protocol dispatch to registered external processes, missing/exited external plugin process handling, Devices-to-plugin command mapping, terminal outcome mapping, and Devices module DI selection for plugin-backed execution.
- Plugin device command execution contract added to `OpenLineOps.Plugin.Abstractions` through `IOpenLineOpsDeviceCommandPlugin`, request/result records, and terminal outcomes so plugin authors can implement device commands against a stable shared contract.
- First-party external plugin host SDK loop added in Plugins Infrastructure. It reads JSON-lines requests from stdin, writes protocol responses to stdout, maps platform invocation DTOs into plugin contract DTOs, rejects plugins that do not implement the device command contract, and isolates protocol errors from command results.
- External plugin host loader added. It reads package manifests, enforces entry assembly path confinement to the package directory, loads the entry assembly inside the child process, constructs the configured plugin type, and verifies manifest id consistency before serving commands.
- `src/OpenLineOps.PluginHost` executable added and included in both solution files. It is a thin process entry point around the external plugin host loader and protocol loop.
- Plugin host tests cover command protocol execution against a real plugin contract implementation, rejection of lifecycle-only plugins, unsupported protocol messages, manifest-driven plugin construction, and entry assembly path confinement.
- Plugin process command declarations added to `OpenLineOps.Plugin.Abstractions` through `PluginProcessCommandDefinition`, and the process-node execution contract added through `IOpenLineOpsProcessNodePlugin`, request/result records, and terminal outcomes including canceled commands.
- Compatible plugin process command inventory added in Plugins Application. It lists concrete process commands only from compatible manifests and supports capability plus command-name lookup for runtime command execution.
- Plugin manifest validation now covers process command definitions, including command id/name/capability requirements, undeclared capability references, duplicate process command declarations, invalid timeouts, invalid retry counts, and the rule that process commands belong to `PluginKind.ProcessNode`.
- External plugin process invocation now supports process commands through the same registered external process boundary used for device commands.
- The first-party plugin host JSON-lines protocol now supports `process-command` and `process-command-result` envelopes alongside device commands, maps invocation DTOs to plugin process contract DTOs, rejects plugins that do not implement the process-node contract, and isolates plugin exceptions into command failure results.
- Runtime Infrastructure now includes `PluginRuntimeCommandExecutor`, which maps `RuntimeCommandExecutionContext` into process plugin command invocations by using compatible plugin process command inventory metadata and maps plugin terminal outcomes back to runtime command outcomes.
- Runtime module can select process plugin command execution through `OpenLineOps:Runtime:CommandExecutor=Plugin`, with `ExternalPlugin`, `PluginProcess`, and `ProcessPlugin` accepted as aliases. Devices module preserves this runtime plugin selection when the device bridge module is also loaded.
- Tests cover compatible process command inventory, process command manifest validation, external process invoker dispatch, plugin host protocol execution against a process-node plugin implementation, Runtime-to-plugin command mapping, terminal outcome mapping, and Runtime/Devices module DI selection for plugin-backed process-node execution.
- External plugin sandbox options added for trusted package enforcement, least-privilege policy requirements, isolation mode metadata, optional identity/container metadata, maximum command timeout, and process-tree termination on command timeout.
- External process activation now enforces entry assembly SHA-256 trust when configured, rejects packages without a matching trusted hash, rejects plugins that require least-privilege execution without an identity or container policy, and passes sandbox metadata into the plugin host environment.
- Default external process runner now clamps command execution time to the configured sandbox maximum and terminates the plugin process tree when a command exceeds that effective timeout.
- External plugin process operational event sink added for startup, trust rejection, sandbox rejection, startup exit/failure, command timeout, and process kill events. The default sink is no-op, with tests using a capturing sink.
- Sandbox hardening tests cover trusted package rejection, trusted hash acceptance, least-privilege policy rejection, sandbox environment propagation, and process event emission for trust and sandbox policy failures.
- Durable SQLite external plugin process event log added in Plugins Infrastructure. It implements the operational event sink contract and provides a stable query read model filtered by plugin id, event kind, occurrence time range, skip, and take.
- Event log persistence tests prove external plugin process events survive a new log instance and that plugin/kind/time filters with stable pagination return the expected operational records.
- Signed package verification added beyond configured entry assembly SHA-256 trust hashes. External plugin trust policy can now require an RSA-SHA256 package signature file, validate it against configured trusted PEM public keys, and bind the signature to plugin identity, version, kind, manifest hash, entry assembly metadata, and entry assembly SHA-256.
- Signed package tests prove unsigned packages are rejected when signature policy is enforced, valid signed packages can be accepted without a configured fixed hash, and tampered entry assemblies fail signature verification before process activation.
- Container runtime process launch added for external plugins. When sandbox isolation is `Container`, `Docker`, or `Podman`, the default system diagnostics runner now starts the configured container runtime instead of the plugin host directly, mounts the plugin package read-only into the container, maps manifest and entry assembly paths into container paths, passes plugin sandbox metadata through `--env`, and can apply `--user`, `--network none`, `--security-opt no-new-privileges`, `--cap-drop ALL`, `--pids-limit`, and additional configured run arguments.
- Container launch tests prove external-process mode remains unchanged, container mode builds a real `docker run`/`podman run` command with read-only package mount and container path rewriting, Podman and Docker defaults are selected correctly, and container isolation without a configured image is rejected before activation.
- OS-native least-privilege account launcher added for non-container plugin execution. When sandbox isolation is `LeastPrivilegeIdentity`, `Sudo`, or `RunAs`, the default runner can launch through a low-privilege wrapper instead of starting the plugin host directly; Unix-style default launch uses `sudo -n -u <identity> -- env ... <plugin-host>`, and deployments can provide a custom launcher executable and argument template for platform-specific account isolation.
- Least-privilege launcher tests prove ordinary external-process startup remains unchanged, sudo-style startup carries plugin environment variables into the low-privilege child process, custom launcher templates are supported, and missing least-privilege identity is rejected before activation.

Delivered on 2026-06-30 (plugin management API slice):

- Added `modules/OpenLineOps.Plugins.Api` as the API-facing plugin management module.
- Registered the Plugins API module in the ASP.NET Core host and added the `plugins-v1` OpenAPI group plus `/api/plugins` route constant.
- Added `/api/plugins/overview` for discovered packages, manifest validation state, compatible capabilities, compatible device commands, compatible process commands, and recent external process events.
- Added `/api/plugins/lifecycle/start` and `/api/plugins/lifecycle/stop` for manifest-only, assembly-load-context, or external-process activator lifecycle execution based on configuration.
- Added `/api/plugins/process-events` with plugin id, event kind, skip, and take filters over the external plugin process event log.
- Default host configuration discovers sample packages from `samples/plugins`, uses `ManifestOnly` activation for local desktop safety, and persists plugin process events to SQLite when external-process activation is enabled.
- Plugin management inventories are registered for the management API by default but are exposed to device/runtime route validation only when `OpenLineOps:Plugins:RegisterRoutingInventories=true`, preserving existing simulator behavior unless plugin routing is explicitly selected.
- Added host-level API tests covering sample plugin overview, lifecycle start/stop, and process-event query validation.

Remaining Milestone 4 work:

- Optional real Docker/Podman and OS account integration profiles once those runtimes and test identities are available in the workspace.

Key tests:

- Invalid manifest rejected.
- Invalid plugin command definition rejected.
- Invalid plugin entry assembly or entry type rejected.
- Plugin initialization failure handled.
- Command execution produces terminal result.
- Device definition and instance persistence round trips.
- Device disconnect changes health state.

Exit criteria:

- A runtime process can execute at least one command through a plugin boundary.

### Milestone 5: Traceability And Audit

Status: completed for the current planned scope.

Goal: make production results searchable and exportable.

Deliverables:

- Trace write model.
- Query read model.
- Measurement and artifact records.
- Audit trail.
- Export API.
- Local development storage implementation.

Delivered on 2026-06-29:

- Added the Traceability bounded context projects:
  - `OpenLineOps.Traceability.Api`
  - `OpenLineOps.Traceability.Domain`
  - `OpenLineOps.Traceability.Application`
  - `OpenLineOps.Traceability.Infrastructure`
  - `OpenLineOps.Traceability.Tests`
- Added `TraceRecord` as the initial trace aggregate for completed runtime output.
- Added mandatory trace links for runtime session, serial number, batch, station, fixture, process definition, process version, configuration snapshot, recipe snapshot, device, judgement, completion time, and operator/system actor.
- Added `MeasurementRecord`, `ArtifactRecord`, `AuditEntry`, `ResultJudgement`, and artifact kind metadata.
- Added `ITraceRecordRepository` and `TraceRecordQuery` with bounded pagination through the shared paging abstraction.
- Added `InMemoryTraceRecordRepository` for application development and fast tests.
- Added `SqliteTraceRecordRepository` for local development storage:
  - Stores the full aggregate document as JSON.
  - Extracts serial, batch, station, fixture, runtime session, process, configuration, recipe, judgement, and completion fields into relational columns.
  - Orders query results by `completed_at_utc` and `trace_id` for deterministic pagination.
  - Persists artifact metadata only; artifact binary storage remains abstracted behind `StorageKey`.
- Added `TraceRecordService` as the application use-case boundary for creating, querying, reading, and exporting trace records.
- Added Traceability API endpoints and OpenAPI group:
  - `POST /api/traceability/records`
  - `GET /api/traceability/records`
  - `GET /api/traceability/records/{traceRecordId}`
  - `GET /api/traceability/records/{traceRecordId}/export`
- Added trace export package response format `openlineops.trace-package.v1` for JSON trace packages.
- Added `OpenLineOps:Traceability:Persistence` configuration with in-memory default and SQLite support.
- Added focused tests for required trace links, measurement/artifact/audit attachment, stable pagination, SQLite round trip, and query filters.
- Added API integration tests for trace creation, paginated search, detail lookup, export, not-found, validation, and API explorer group registration.
- Added runtime trace metadata on `RuntimeSession`, simulated runtime start requests, and process-launched runtime sessions so production identity can enter the runtime boundary before trace creation.
- Added a runtime domain-event subscriber in the Traceability API composition layer that listens for completed runtime sessions, reloads the completed session through the runtime repository port, and creates a deterministic one-to-one `TraceRecord`.
- Runtime-generated trace records include runtime session id, serial, batch, station, fixture, process definition/version, configuration snapshot, recipe snapshot, device id, generated judgement, completed command measurements, and a system audit entry.
- Added tests proving runtime trace metadata persists through SQLite and a completed simulated runtime session can be queried and exported through Traceability APIs.
- Added `ITraceArtifactStorage` as the application-layer artifact binary storage port so Traceability can store artifact content without coupling the trace aggregate to file-system details.
- Added a local file artifact storage adapter with path confinement, deterministic storage keys, SHA-256 calculation, optional expected-hash validation, and common media-type inference for downloads.
- Added `POST /api/traceability/artifacts` and `GET /api/traceability/artifacts/{storageKey}` so clients can upload artifact content first, then reference returned `storageKey`, `sizeBytes`, and `sha256` metadata from trace records.
- Added `OpenLineOps:Traceability:ArtifactStorage` configuration with `LocalFile` as the default provider and configurable root path.
- Added tests proving local artifact storage write/read behavior, storage-key path traversal rejection, and host-level multipart upload plus download.
- Added `ITraceJudgementGenerator` as the application-layer rule boundary for deriving trace judgement when clients or runtime integration do not provide an explicit judgement.
- Added configurable judgement rules through `OpenLineOps:Traceability:Judgement`:
  - `DefaultJudgement`, default `Passed`.
  - `FailWhenAnyMeasurementFailed`, default `true`.
  - `UnknownWhenAnyMeasurementIndeterminate`, default `false`.
  - `UnknownWhenNoMeasurements`, default `false`.
- Trace creation now treats explicit `judgement` as an override, otherwise generates judgement from configured rules before creating the aggregate.
- Runtime completion trace generation no longer hardcodes `Passed`; it lets the Traceability application rule decide.
- Added tests proving explicit judgement precedence, failed-measurement generated `Failed`, configured no-measurement `Unknown`, invalid configured default validation, and API-level missing-judgement generation.
- Added `ITraceReadModelService` for Electron-oriented Traceability read models.
- Added station dashboard read model with judgement counts, first/last completion timestamps, truncation indicator, and recent trace rows including measurement, failed-measurement, and artifact counts.
- Added engineering trace search read model with richer row shape and facets for judgements, stations, devices, and process versions.
- Added read-model API endpoints:
  - `GET /api/traceability/read-models/station-dashboard`
  - `GET /api/traceability/read-models/engineering-search`
- Extended trace query filters to include process definition, process version, configuration snapshot, recipe snapshot, device, and judgement.
- Added SQLite indexes and tests for process/device/judgement filters.
- Added application and host-level API tests proving dashboard counts, recent trace ordering, engineering search rows, and facet counts.
- Added `PostgresTraceRecordRepository` for deployment-mode trace persistence:
  - Stores the full trace aggregate snapshot as a PostgreSQL `jsonb` document.
  - Extracts runtime session, serial, batch, station, fixture, process, configuration, recipe, device, judgement, and completion columns for indexed search.
  - Uses the same stable ordering contract as SQLite: `completed_at_utc`, then `trace_id`.
- Traceability module dependency injection can select `PostgreSql` persistence through `OpenLineOps:Traceability:Persistence`, with `Postgres` and `PostgreSQL` accepted as aliases.
- Configuration tests cover required PostgreSQL connection strings and prove repository construction does not open a database connection.
- Optional Testcontainers-backed PostgreSQL integration test proves a trace record graph can be saved, reloaded through a new repository instance, and queried by process version, device, and judgement when `OPENLINEOPS_RUN_POSTGRES_INTEGRATION=1` is set.

Remaining tasks:

- None for the current Milestone 5 scope. Before deployment use, run the optional PostgreSQL integration profile in a Docker-enabled environment.

Key tests:

- Trace is linked to runtime session, configuration snapshot, station, and process version.
- Pagination is stable.
- Artifact metadata persists without coupling to storage implementation.

Exit criteria:

- A completed runtime session can be searched and exported.

### Milestone 6: Monitoring And Realtime

Status: completed for the current planned scope.

Goal: give operators and engineers live visibility into execution.

Deliverables:

- SignalR runtime progress hub.
- Station status projection.
- Alarm model and acknowledgement workflow.
- Runtime timeline query.
- API integration tests for realtime events.

Delivered on 2026-06-29:

- Added `IRuntimeMonitoringService` and `RuntimeMonitoringProjection` inside the Runtime application boundary.
- Runtime monitoring projection subscribes to runtime domain events and maintains:
  - latest station/session status rows for reconnect resync;
  - per-session timeline entries with event sequence, event name, entity kind, status transitions, reason, severity, and code;
  - runtime alarm rows derived from runtime incidents.
- Added alarm acknowledgement workflow that records `AcknowledgedBy` and `AcknowledgedAtUtc`, hides acknowledged alarms by default, and exposes acknowledged alarms when requested.
- Added Runtime monitoring HTTP endpoints:
  - `GET /api/runtime/monitoring/stations`
  - `GET /api/runtime/monitoring/sessions/{sessionId}/timeline`
  - `GET /api/runtime/monitoring/alarms`
  - `POST /api/runtime/monitoring/alarms/{alarmId}/acknowledgements`
- Added `RuntimeProgressHub` at `/hubs/runtime-progress` for SignalR runtime progress.
- Added SignalR client contract messages:
  - `RuntimeEvent`
  - `StationStatusChanged`
  - `AlarmRaised`
  - `AlarmAcknowledged`
- Runtime SignalR subscriber broadcasts from the same runtime domain-event stream used by traceability and monitoring projection.
- Added tests proving runtime events update station status, timeline, and alarm projection; alarm acknowledgement records actor and timestamp; HTTP monitoring endpoints expose resync state; and SignalR publishes station/timeline updates during simulated runtime execution.

Key tests:

- Runtime events update live projection.
- Reconnect can resync from latest state.
- Alarm acknowledgement records user and timestamp.

Exit criteria:

- Electron UI can render live station/session state without direct runtime coupling.

### Milestone 7: Electron Application

Status: in progress.

Goal: build the desktop application around the backend contracts.

Recommended stack:

- Electron shell.
- React or Vue frontend.
- TypeScript.
- Local API client generated or typed from OpenAPI contracts.
- SignalR client for live runtime updates.

Initial screens:

- Workspace/project selector.
- Station runtime dashboard.
- Process definition editor.
- Device configuration view.
- Trace query view.
- Plugin management view.

Key constraints:

- Electron does not own business rules.
- Electron should not read backend databases directly.
- Desktop startup should manage backend process lifecycle explicitly.
- API base URL, logs, and local data path must be configurable.
- The desktop shell should be project-first: start with new/open/recent projects, keep an active project context, and scope workbenches to that project.
- A site layout editor should provide a top-down canvas for applications, equipment nodes, modules, slot groups, slots, devices, connections, and labelled zones, with future 3D rendering kept outside the domain model.
- Flow editing defaults to Blockly for script authoring and allows manual Python code editing for advanced users.
- Python script validation and execution must use the in-repository `lib/pythonscript` component unless a future ADR explicitly replaces that integration.

Exit criteria:

- Electron can start the backend, show platform info, display health, and subscribe to live runtime simulation events.

Delivered on 2026-06-29:

- Added `apps/desktop` as the Electron desktop shell using React, TypeScript, Vite, and SignalR client.
- Added secure Electron process boundary:
  - renderer has no Node.js access;
  - `contextIsolation` remains enabled;
  - preload exposes only explicit desktop APIs through `contextBridge`;
  - backend lifecycle and HTTP proxy calls are handled by the main process.
- Added backend lifecycle controls from Electron main process:
  - default API base URL `http://localhost:5135`;
  - configurable `OPENLINEOPS_API_BASE_URL`;
  - configurable `OPENLINEOPS_API_PROJECT`;
  - configurable `OPENLINEOPS_REPO_ROOT`;
  - recent backend log ring buffer surfaced to renderer.
- Added first desktop dashboard screen:
  - backend start/stop;
  - platform and health status;
  - runtime station status;
  - SignalR runtime timeline;
  - alarm queue with acknowledgement;
  - trace record preview from Traceability APIs;
  - navigation shell for Dashboard, Engineering, Processes, Devices, Trace, and Plugins.
- Added SignalR client subscription to `/hubs/runtime-progress` for station status, runtime timeline, alarm raised, and alarm acknowledged events.
- Added API CORS policy for desktop development origins, configurable through `OpenLineOps:Desktop:AllowedOrigins`.
- Added API integration test proving the runtime SignalR hub allows the default Vite desktop origin.
- Added `apps/desktop/README.md` with desktop commands and runtime boundary notes.
- Added automated Electron smoke test command `npm run smoke:e2e`:
  - starts Vite preview on a dynamic local port;
  - launches Electron through its real main/preload/renderer boundary;
  - starts the local .NET API from the Electron main process;
  - waits for backend health and SignalR connection;
  - runs a simulated runtime session from the rendered UI;
  - verifies runtime station state, SignalR event counts, simulated completion text, and trace row output.
- Fixed the Electron package entry point to `dist-electron/main/main.js`.
- Hardened Electron `api:request` so temporary backend connection failures return structured API errors instead of throwing IPC handler exceptions.
- Improved SignalR lifecycle so the renderer connects after the backend becomes healthy and retries failed starts.

Verification on 2026-06-29:

- Desktop dependency install passed after retrying Electron binary download with `ELECTRON_MIRROR=https://npmmirror.com/mirrors/electron/`.
- Electron was upgraded to `^42.5.1` after `npm audit` reported high-severity advisories against the earlier Electron range.
- Desktop `npm run typecheck` passed.
- Desktop `npm run build` passed.
- Desktop `npm audit --audit-level=high --registry=https://registry.npmjs.org` passed with zero reported vulnerabilities.
- Local renderer preview returned HTTP 200 through `npx vite preview --host 127.0.0.1 --port 4173`.
- Visual verification used system Chrome headless at 1440x900 because Playwright browser download did not finish in this workspace.
- Screenshot artifact reviewed at `output/playwright/desktop-dashboard.png`; the first dashboard slice rendered nonblank with no obvious text overlap or empty primary surface.

Verification on 2026-06-30:

- Desktop `npm run smoke:e2e`: passed. It verified Electron startup, backend process startup, health refresh, SignalR connection, simulated runtime session completion, station update events, runtime timeline events, trace row output, process graph save/publish, and Engineering snapshot publication.

Delivered on 2026-06-30 (process editor slice):

- Replaced the Processes placeholder with an API-backed `ProcessWorkbench` in the Electron renderer.
- Added desktop process-definition API client contracts for list, create, validate, and publish.
- Added a Blockly-first PythonScript authoring workflow:
  - visual block-style fields for result output key, script input payload, and trace metadata inclusion;
  - generated Python source preview;
  - persisted Blockly workspace JSON;
  - manual Python code mode toggle for advanced editing.
- The workbench creates a linear Start -> PythonScript -> End process definition using the same backend Process API contracts as other clients.
- The workbench can validate and publish the saved process definition, so publish-time Python syntax validation still happens in the backend.
- Extended the Electron smoke test so it opens Processes, creates a Blockly-default PythonScript definition, publishes it, and asserts the saved/published UI state.

Delivered on 2026-06-30 (process graph editor slice):

- Expanded the Electron Processes workbench from a fixed linear template into a graph draft editor.
- Added a node toolbox for `PythonScript`, `Command`, `Decision`, `Delay`, and `End` nodes while preserving the required single `Start` node.
- Added node selection, node metadata editing, command capability/command/timeout editing, PythonScript timeout/version/input editing, and selected-node deletion with simple transition bridging.
- Added a transition editor for source, target, label, add, and remove operations.
- Added full process-definition loading through `GET /api/process-definitions/{id}` so saved definitions can be inspected in the graph editor.
- Kept PythonScript authoring Blockly-first and preserved manual Python editing mode; Blockly mode now captures result key, input payload, status, node trace inclusion, and timestamp placeholder fields.
- Extended the desktop smoke test to add a `Command` node before saving and publishing a Blockly-default PythonScript process definition.

Delivered on 2026-06-30 (official Blockly automation workspace slice):

- Replaced the hand-built Blockly-compatible script block surface with the official `blockly` workspace package in the Electron Processes workbench.
- Added OpenLineOps automation blocks for axis movement, light output, motor rotation, wait, and result output.
- Blockly workspace state is persisted through the existing `blocklyWorkspaceJson` process-node contract, and generated Python source is still stored and validated through the existing PythonScript publish path.
- Blockly-generated Python writes a structured `automation_plan` into `result`.
- Kept manual Python code mode for advanced users.
- Lazy-loaded the Processes workbench and split Blockly into a separate Vite chunk so the runtime dashboard does not load Blockly until process editing is opened.
- Disabled Blockly's trashcan plugin in the embedded Electron workspace because this Blockly build registers the delete-area plugin globally and throws on repeated injection; node deletion remains available through the OpenLineOps graph inspector.
- Extended the Electron smoke test so it verifies the real Blockly workspace container before saving and publishing the Blockly-default PythonScript process definition.

Delivered on 2026-06-30 (runtime automation plan dispatch slice):

- Added `RuntimeAutomationPlanDispatcher` in the Runtime application layer to translate Blockly-generated `automation_plan` actions into child runtime commands.
- Mapped initial automation actions to runtime command contracts: `axis.move` -> `motion.axis` / `MoveAxis`, `io.light` -> `io.light` / `SetLight`, `motor.rotate` -> `motion.motor` / `RotateMotor`, and `flow.wait` as a local runtime delay.
- Runtime and Devices configurable command executors now run the automation dispatcher after successful PythonScript execution, so actions flow through the existing simulator, device-backed, or plugin-backed command execution path according to runtime configuration.
- Script result payloads now include `automation_dispatch` with per-action sequence, type, outcome, payload, and reason.
- Runtime dispatch stops on the first failed, rejected, timed out, or canceled automation action and returns that terminal result to the parent command.
- Added Runtime tests covering no-plan passthrough, command mapping, local wait execution, unsupported action rejection, and stop-on-rejection behavior.

Delivered on 2026-06-30 (modular Blockly block catalog slice):

- Added a Process Blockly block catalog application service with built-in OpenLineOps blocks and user registration for custom blocks.
- Added `/api/process-blocks` endpoints to list available Blockly block definitions and register custom block definitions.
- Blockly block definitions now carry Blockly JSON plus a Python code template instead of requiring hard-coded frontend generator functions.
- The Electron Processes workbench loads the block catalog from the backend, dynamically registers Blockly blocks through `defineBlocksWithJsonArray`, and registers Python generators through `pythonGenerator.forBlock`.
- Python template placeholders support string, number, and raw insertion forms: `{{FIELD}}`, `{{number:FIELD}}`, and `{{raw:FIELD}}`.
- Added a compact Block Catalog panel in the Processes workbench for registering a user-defined block from the UI.
- Extended unit, API, and Electron smoke coverage for built-in block listing and user-defined block registration.

Delivered on 2026-06-30 (persistent Blockly block catalog slice):

- Replaced the transient catalog implementation with a Process application service backed by a Blockly block definition repository port.
- Added in-memory, SQLite, and PostgreSQL repository adapters for user-registered Blockly block definitions, reusing the existing Processes persistence provider selection.
- Added versioned custom block storage. Re-registering the same custom `blockType` creates the next version while built-in blocks remain protected from overwrite.
- Added `GET /api/process-blocks/{blockType}/versions` for version history retrieval; normal block listing continues to return the latest version plus built-in blocks.
- Extended Processes and API tests for persisted custom block restoration, latest-version selection, version history ordering, built-in overwrite rejection, and missing block history handling.
- Updated the Electron renderer contracts, block catalog display, and API client to include block version metadata.

Delivered on 2026-06-30 (plugin manifest Blockly generation slice):

- Added a `IProcessBlocklyBlockCatalogSource` extension point so the Process Blockly catalog can merge built-in blocks, read-only generated blocks, and persisted user-defined blocks without coupling Processes.Application to plugin infrastructure.
- Added a Processes API adapter that reads compatible plugin device and process command inventories and generates read-only Blockly block definitions from manifest command metadata.
- Manifest-generated blocks emit Python templates that append `command.execute` automation actions with explicit capability, command, payload, command definition id, plugin metadata, and timeout.
- Extended `RuntimeAutomationPlanDispatcher` to map `command.execute` actions into normal runtime child commands and to honor per-action `timeout_ms`.
- User registration now rejects generated block overrides, and version lookup returns generated block metadata as a read-only single-version entry.
- Extended Processes, Runtime, and API tests for generated catalog source listing, override protection, explicit command dispatch, and sample plugin manifest block discovery through `/api/process-blocks`.

Delivered on 2026-06-30 (desktop Blockly block version history slice):

- Added a custom Blockly block version browser to the Electron Processes Block Catalog panel.
- The panel selects user-registered block types only; built-in and manifest-generated read-only blocks remain excluded from restore controls.
- Restoring a version uses the existing registration endpoint to re-register the selected older definition as the latest version, preserving immutable version history instead of mutating prior rows.
- Registration and restore flows now refresh the backend catalog and selected block history after successful writes, so the UI stays aligned with the persisted process block repository.
- Extended the Electron smoke test to register a custom block twice, verify v1/v2 history, restore v1 as v3, and continue through process publication and runtime launch.

Delivered on 2026-06-30 (published process launch slice):

- Added a desktop Processes runtime launch panel for published process definitions.
- Added renderer contracts and API client support for `POST /api/process-definitions/{processDefinitionId}/runtime-sessions`.
- The launch panel captures the required engineering `configurationSnapshotId` plus serial, batch, fixture, device, and actor trace metadata.
- Runtime launch is disabled until a backend is healthy, a process definition is selected and published, and a configuration snapshot id is present.
- Successful launches show the resulting runtime session id, status, completed step count, command count, and incident count in the Processes workbench.
- Extended the Electron smoke test to create a matching engineering recipe, station profile, workspace, project, and published configuration snapshot through the backend API, then start the published process from the rendered UI.

Delivered on 2026-06-30 (engineering desktop workbench slice):

- Replaced the Engineering placeholder view with an API-backed `EngineeringWorkbench` in the Electron renderer.
- Added desktop engineering API client contracts for workspaces, projects, recipes, station profiles, recipe publication, and configuration snapshot publication.
- The Engineering workbench can load current engineering resources from the backend and display workspace, project, recipe, and station profile lists.
- Added a compact runtime-configuration seed workflow that creates a workspace, project, recipe, published recipe, station profile with device binding, and published configuration snapshot for a selected published process definition.
- The workbench shows the published snapshot id, process definition, recipe version, station profile, and snapshot status after publication.
- Extended the Electron smoke test so it opens Engineering, publishes a configuration snapshot through the rendered UI, and asserts the resulting published snapshot state.

Delivered on 2026-06-30 (devices desktop workbench slice):

- Added Devices management application contracts for creating device definitions, registering station-bound device instances, listing definitions/instances, and changing instance connection state.
- Added Devices API controllers under `/api/devices` for definition registration, instance registration, connect, disconnect, fault, and fault reset workflows.
- Registered the Devices API module in the ASP.NET Core host and added host-level API tests for the device configuration workflow.
- Replaced the Devices placeholder view with an API-backed `DevicesWorkbench` in the Electron renderer.
- The Devices workbench can seed a loopback-style device definition, register a device instance, select instances, and manage connected, disconnected, faulted, and reset states.
- Extended the Electron smoke test so it opens Devices, registers a device bundle through the rendered UI, and connects the registered instance.

Delivered on 2026-06-30 (trace desktop workbench slice):

- Replaced the Trace placeholder view with an API-backed `TraceWorkbench` in the Electron renderer.
- Added desktop Traceability API client contracts for engineering trace search, trace record details, and trace export packages.
- The Trace workbench can query engineering trace read models, show judgement/station/device/process-version facets, select a trace row, inspect measurements/artifacts/audit entries, and load an export package.
- Extended the Electron smoke test so it opens Trace after a simulated runtime session, verifies search results, loads a selected trace detail, and loads the trace export package.

Delivered on 2026-06-30 (plugins desktop workbench slice):

- Replaced the Plugins placeholder view with an API-backed `PluginsWorkbench` in the Electron renderer.
- Added desktop Plugins API client contracts for management overview, lifecycle records, capabilities, command inventory, validation issues, and external process events.
- The Plugins workbench can discover sample plugin packages, inspect manifest validation details, list compatible capabilities and commands, start/stop the plugin lifecycle, and inspect external process events.
- Extended the Electron smoke test so it opens Plugins, verifies the Loopback Device Sample manifest, starts the plugin lifecycle, and stops it again from the rendered UI.

Delivered on 2026-07-09 (desktop loop-policy smoke coverage slice):

- Added stable desktop transition-editor test ids for transition id, source, target, label, loop policy, max traversal, and add controls.
- Extended the Electron smoke test to create a legal Decision-controlled counted retry loop from `decision-1` back to the Blockly PythonScript node.
- The smoke test now edits `loopPolicy=Counted` and `maxTraversals=2` through the rendered UI, saves the process definition, verifies the persisted transition through the backend API, then continues through publish and runtime launch.
- Hardened the smoke script to discover generated transition ids from the rendered DOM instead of depending on internal transition-id suffix generation.

Delivered on 2026-06-30 (NetDevPack DDD foundation slice):

- Wired `shared/OpenLineOps.Domain.Abstractions` to the local `lib/NetDevPack` project prepared in this repository.
- Aligned the DDD foundation with OpenLineOps modular DDD conventions: aggregate roots satisfy NetDevPack `IAggregateRoot`, repositories expose NetDevPack Unit of Work semantics, and integration events support interface or attribute metadata.
- Preserved OpenLineOps strong typed aggregate IDs instead of replacing entities with NetDevPack's `Guid Id` entity base directly.
- Added a strong typed `IAggregateRepository<TAggregate, TId>` as the project-wide aggregate repository contract.
- Added EventBus abstractions for `IIntegrationEvent`, `IntegrationEventAttribute`, `IIntegrationEventPublisher`, `IIntegrationDtoConverter`, `IntegrationDtoConverterRegistry`, and integration event descriptor creation.
- Adjusted the local NetDevPack project to build under the .NET 10 SDK and root build settings without global-using or central-package-management conflicts.
- Extended Domain.Abstractions tests for NetDevPack aggregate compatibility, Unit of Work repository contracts, integration event metadata discovery, integration DTO conversion, and NetDevPack value object equality.

Delivered on 2026-06-30 (Infrastructure Data.Core foundation slice):

- Added `shared/OpenLineOps.Infrastructure.Data.Core` as the shared EF Core data foundation for future relational bounded contexts.
- Added `BaseDbContext` with Unit of Work commit semantics, OpenLineOps domain event dispatch, NetDevPack domain event dispatch, and optional integration event publishing for both OpenLineOps and NetDevPack domain event models.
- Added `BaseRepository<TContext, TAggregate, TId>` for strong typed aggregate IDs while still exposing NetDevPack `IUnitOfWork`.
- Added `StronglyTypedIdValueConverter<TId, TValue>` and `HasStronglyTypedIdConversion<TId, TValue>()` so EF mappings can keep domain-specific identifiers without hand-written conversion code in every context.
- Added OpenLineOps domain event dispatcher contracts and an `IHasDomainEvents` aggregate event surface so EF infrastructure can collect and clear domain events without reflection.
- Added `tests/OpenLineOps.Infrastructure.Data.Core.Tests` covering event dispatch, OpenLineOps and NetDevPack integration event publishing, strong typed ID conversion, repository query, and repository removal behavior.
- Added ADR-0006 to record that strong typed domain identifiers are intentional architecture, not a temporary compatibility layer.

Delivered on 2026-06-30 (EF/Data.Core bounded-context living template slice):

- Added `samples/bounded-contexts/OpenLineOps.SampleInspection.*` as a compileable living template for new DDD bounded contexts.
- The sample follows the Domain/Application/Infrastructure split: aggregate root, strong typed stable identifier, domain events, application-owned repository port, EF `DbContext`, relational entity configuration, DI registration, and Data.Core-backed repository.
- Added `tests/OpenLineOps.SampleInspection.Tests` using SQLite in-memory relational persistence to verify aggregate round trip, Unit of Work commit, OpenLineOps domain event dispatch, and shared strong typed ID conversion.
- Added `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.EntityFrameworkCore.Sqlite`, and explicit SQLite native bundle central package entries for relational EF template verification.
- Added `samples/bounded-contexts/README.md` documenting the template shape and the rule that persisted IDs require aliases and migrations rather than silent renames.

Delivered on 2026-06-30 (Devices production EF/Data.Core adapter slice):

- Added `modules/OpenLineOps.Devices.Infrastructure/Persistence/Ef` with `DevicesDbContext`, relational mappings, and Data.Core-backed repositories for `DeviceDefinition` and `DeviceInstance`.
- Added `DevicePersistenceProviders.EfSqlite` and Devices module DI registration for `OpenLineOps:Devices:Persistence:Provider=EfSqlite`.
- Mapped strong typed device identifiers through `HasStronglyTypedIdConversion<TId, string>()`, including child command/capability identifiers and `DeviceInstance.DefinitionId`.
- Mapped `DeviceDefinition` capability and command collections as EF owned collections and `DeviceInstance.Endpoint` as an owned value object.
- Added a private EF materialization constructor and private setters to `DeviceInstance` so EF can hydrate the owned endpoint value object without weakening public domain behavior.
- Extended Devices tests to verify EF SQLite aggregate round trip, station query, update path, Data.Core Unit of Work commit, and OpenLineOps domain event dispatch/clearing on a real production bounded context.
- Extended API DI tests to verify `Provider=EfSqlite` selects the Data.Core-backed repository implementations.

Delivered on 2026-06-30 (bounded-context scaffolder slice):

- Added `tools/OpenLineOps.BoundedContext.Scaffolder` as a .NET 10 repo-local command for generating new DDD bounded contexts.
- Updated the command to generate the modular DDD split: `Domain.Shared`, `Domain`, `Application.Contract`, `Application`, `Infra.Data`, `Infra.CrossCutting.IoC`, `Api`, and Tests.
- Generated bounded contexts now keep cross-boundary DTOs in `Domain.Shared`, application DTO/service contracts in `Application.Contract`, aggregate repositories in `Domain`, EF/Data.Core adapters in `Infra.Data`, and module composition in `Infra.CrossCutting.IoC`.
- Generated API projects expose controller assembly registration for the central ASP.NET Core host without putting domain decisions in controllers.
- Added optional `--update-solution` and repeatable `--solution` support so generated module projects and tests can be added to `.sln`/`.slnx` files automatically.
- Added `tests/OpenLineOps.BoundedContext.Scaffolder.Tests` covering file generation, modular DDD project boundaries, Data.Core/EF template content, overwrite protection, argument parsing, solution update command construction, and identifier validation.
- Verified the command with `--help`, generated a smoke `Quality/InspectionPlan` context under `output/scaffolder-smoke`, built the generated API/Ioc project chain with zero warnings, and ran the generated SQLite persistence/application-service tests.
- Added `docs/bounded-context-scaffolding.md` and linked the command from the README and living-template notes.

Delivered on 2026-06-30 (Operations alarm bounded-context slice):

- Added the production `OpenLineOps.Operations.*` bounded context using the modular DDD project split: `Domain.Shared`, `Domain`, `Application.Contract`, `Application`, `Infra.Data`, `Infra.CrossCutting.IoC`, `Api`, and Tests.
- Added `Alarm` as the first Operations aggregate with stable strong typed `AlarmId`, station/source metadata, severity, status, title, description, raised time, acknowledgement metadata, resolution metadata, and explicit `Raised -> Acknowledged -> Resolved` lifecycle behavior.
- Added Operations shared contracts for `AlarmSeverity`, `AlarmStatus`, and `AlarmRaisedIntegrationDto`.
- Added domain events for alarm raised, acknowledged, and resolved, plus the raised-event to integration DTO converter.
- Added application contracts and app service use cases for raising alarms, querying by id, querying open station alarms, acknowledging alarms, and resolving alarms.
- Added EF/Data.Core persistence through `OperationsDbContext`, `EfAlarmRepository`, strong typed ID conversion, station/status indexing, and SQLite-safe `DateTimeOffset` ticks conversion for ordering.
- Added `OperationsNativeInjectorBootStrapper` with an initial default InMemory registration for the central API host and an explicit `Action<DbContextOptionsBuilder>` overload for tests/future provider selection.
- Added Operations API endpoints under `/api/operations/alarms` and registered the Operations API/module in the central ASP.NET Core host.
- Added Operations tests covering aggregate lifecycle behavior, SQLite/Data.Core persistence, open-station alarm queries, app-service lifecycle use cases, and host-level alarm API lifecycle.

Delivered on 2026-06-30 (integration event alignment slice):

- Generalized `IIntegrationEventPublisher` and `IntegrationEventDescriptor` so integration publishing is not limited to NetDevPack `Event` payloads.
- Added `IIntegrationDtoConverter` and `IntegrationDtoConverterRegistry` as the project-wide converter registry pattern.
- Updated `BaseDbContext` so committed OpenLineOps domain events and NetDevPack domain events are both dispatched locally and then filtered into the optional integration publisher when marked by `IIntegrationEvent` or `IntegrationEventAttribute`.
- Marked `AlarmRaisedDomainEvent` as an integration event and registered `AlarmIntegrationDtoConverter`, keeping `AlarmRaisedIntegrationDto` as the pure cross-boundary DTO in `Domain.Shared`.
- Updated the bounded-context scaffolder so generated modules include integration-event marking, domain-event-to-DTO converter registration, `IntegrationDtoConverterRegistry` IoC registration, and publisher-ready `DbContext` constructors by default.
- Added tests covering OpenLineOps integration event descriptor creation, converter registry conversion, BaseDbContext OpenLineOps integration publishing, Operations alarm event conversion, and generated scaffolder event wiring.
- This slice established the domain contracts and Data.Core hook consumed by the CAP-backed infrastructure adapter.

Delivered on 2026-06-30 (Runtime-to-Operations alarm bridge slice):

- Added `RuntimeIncidentOperationsAlarmSubscriber` in the Operations API integration layer, following the existing Runtime-to-Traceability subscriber pattern.
- Registered the subscriber as an `IRuntimeDomainEventSubscriber` from `AddOpenLineOpsOperationsApi()` so Runtime incident domain events can create durable Operations alarms through the Operations application contract.
- Kept Runtime independent from Operations; the bridge lives in API/module composition, reads the persisted Runtime session through `IRuntimeSessionRepository`, and resolves the scoped `IAlarmAppService` through an explicit service scope.
- Added deterministic alarm IDs in the form `operations.alarm.runtime.incident.{incidentId:N}` and idempotency by checking for an existing Operations alarm before raising.
- Extended `RaiseAlarmRequest` with optional `RaisedAtUtc` so Operations alarms raised from Runtime incidents preserve the incident occurrence time.
- Extended host-level Runtime session API coverage so a failed simulated Runtime session is verified through `/api/operations/alarms/open` as an open Operations alarm with runtime source metadata.

Delivered on 2026-06-30 (CAP EventBus publisher slice):

- Added `shared/OpenLineOps.EventBus` with `CapIntegrationEventPublisher`, converting domain integration events to boundary DTOs before publishing.
- Registered `AddOpenLineOpsEventBus()` in the central API host after bounded-context module registration so registered `IIntegrationDtoConverter` implementations are available to the shared `IntegrationDtoConverterRegistry`.
- Added CAP 10 package wiring with in-memory CAP storage/message queue as the default local profile.
- Added deployment-oriented EventBus options under `OpenLineOps:EventBus` for PostgreSQL CAP storage schema, optional RabbitMQ transport, retry/expiry settings, consumer thread count, and optional CAP dashboard.
- Published messages include event name, converted payload, event version, timestamp, correlation id, aggregate id, and source domain event type headers.
- Added `tests/OpenLineOps.EventBus.Tests` covering domain-event-to-integration-DTO conversion and CAP topic/header publishing behavior.
- This slice provided the base publisher. The later transaction-coordinator slice adds opt-in EF Core/CAP same-transaction support for deployment profiles that require atomic business-row plus outbox-row commits.

Delivered on 2026-06-30 (Devices EfSqlite default and snapshot compatibility slice):

- Changed Devices persistence defaults from ephemeral `InMemory` to Data.Core-backed `EfSqlite`, with `data/openlineops-devices.sqlite` as the default local database path.
- Added explicit Devices persistence configuration to the API host appsettings.
- Kept `Provider=InMemory` and snapshot `Provider=Sqlite` as explicit compatibility choices.
- Added lazy snapshot compatibility import for SQLite tables: `device_definitions.document_json` and `device_instances.document_json` are restored through `DevicePersistenceMapper` and inserted into EF relational tables when missing.
- Preserved EF rows on id conflicts, kept snapshot tables untouched for export/inspection, and documented that stable device ids must not be silently replaced.
- Added `docs/devices-persistence.md` with provider selection, migration/backfill rules, and operational guidance.
- Extended Devices and API DI tests to cover default `EfSqlite`, explicit `InMemory`, and SQLite snapshot import into EF SQLite.

Delivered on 2026-06-30 (CAP EF transaction coordinator slice):

- Added `ITransactionalIntegrationEventPublisher` so normal publishing can keep compatibility failure handling while transaction-bound publishing uses strict failure handling.
- Added Data.Core `IIntegrationEventTransactionCoordinator` as the small EF-facing port consumed by `BaseDbContext`.
- Updated `BaseDbContext` so integration events can be published inside an optional transaction coordinator before local domain-event subscribers are dispatched.
- Added `CapEfCoreIntegrationEventTransactionCoordinator`, which uses CAP's EF Core transaction extension to save aggregate changes and publish integration events in one CAP-aware EF transaction.
- Added `OpenLineOps:EventBus:EnableEfCoreTransactionCoordinator` as an explicit opt-in switch. It should only be enabled when the EF bounded-context storage and CAP storage participate in the same relational database transaction.
- Updated Devices and Operations EF `DbContext` constructors, plus the bounded-context scaffolder template, so generated Data.Core contexts can receive the optional coordinator through DI.
- Added `docs/eventbus-transaction-coordination.md` documenting configuration, runtime behavior, failure semantics, and deployment constraints.
- Added tests covering BaseDbContext transaction-coordinator publishing, CAP publisher strict failure behavior, and scaffolder template wiring.

Delivered on 2026-06-30 (Devices EF migration bootstrap slice):

- Added the `InitialDevicesEfSqlite` EF Core migration and design-time factory for the Devices bounded context.
- Changed Devices `EfSqlite` schema bootstrap from `EnsureCreated` to `MigrateAsync`, while preserving compatibility with EF `EnsureCreated` databases by recording the initial migration in `__EFMigrationsHistory` when the `_ef` schema is already present.
- Kept SQLite JSON snapshot compatibility import intact after migration bootstrap, so `device_definitions` and `device_instances` snapshot tables can still be imported without mutating the snapshot tables.
- Added coverage for upgrading an existing EF `EnsureCreated` Devices database and for preserving pending-migration state after repository access.
- Updated `docs/devices-persistence.md` so the public persistence guidance now treats Devices `EfSqlite` as migration-first.

Delivered on 2026-06-30 (Operations and bounded-context template EF migration slice):

- Added the `InitialOperationsSqlite` EF Core migration and design-time factory for the Operations bounded context.
- Changed Operations SQLite/Data.Core tests from `EnsureCreatedAsync` to `MigrateAsync` and added pending-migration verification for the initial schema.
- Added the `InitialSampleInspectionSqlite` EF Core migration and design-time factory for the SampleInspection living template.
- Changed SampleInspection SQLite/Data.Core tests from `EnsureCreatedAsync` to `MigrateAsync` and added pending-migration verification.
- Updated the bounded-context scaffolder so generated Infra.Data projects include EF Design, SQLite, and the SQLitePCLRaw bundle, plus a design-time factory, deterministic initial migration, migration designer, model snapshot, and migration-based persistence/application-service tests.
- Verified a generated `Quality/InspectionPlan` bounded context under `output/scaffolder-migration-smoke-*`: generated Infra.Data build passed with zero warnings, and generated SQLite persistence/application-service tests passed with 3 tests.

Delivered on 2026-06-30 (Operations EfSqlite default alignment slice):

- Changed Operations persistence defaults from ephemeral `InMemory` to Data.Core-backed `EfSqlite`, with `data/openlineops-operations.sqlite` as the default local database path.
- Added explicit Operations persistence configuration to the API host appsettings while keeping `Provider=InMemory` as an explicit compatibility choice for tests and temporary local runs.
- Added `OperationsPersistenceOptions` and `AddEfSqliteOperationsPersistence()` so CrossCutting IoC selects providers without leaking SQLite-specific setup into application services.
- Added lazy migration bootstrap in `OperationsDbContext`/`EfAlarmRepository` so first-read and first-write paths apply the EF migration before querying or committing alarms.
- Extended Operations/API DI and persistence tests to cover default `EfSqlite`, explicit `InMemory`, and first-write migration bootstrap.

Delivered on 2026-06-30 (Operations PostgreSQL provider profile slice):

- Added `Provider=PostgreSql` for Operations persistence, with `Postgres` and `PostgreSQL` accepted as aliases under `OpenLineOps:Operations:Persistence`.
- Kept Operations on the EF/Data.Core path for PostgreSQL instead of adding a handwritten repository, so alarm domain events still flow through `BaseDbContext`, integration DTO conversion, and the optional CAP EF transaction coordinator.
- Added `AddEfPostgreSqlOperationsPersistence()` backed by `Npgsql.EntityFrameworkCore.PostgreSQL`, with connection-string validation matching the existing Runtime/Processes/Engineering/Traceability PostgreSQL provider style.
- Made Operations alarm timestamp columns use `bigint` for migration-backed relational portability, avoiding PostgreSQL 32-bit integer overflow for `DateTimeOffset.UtcTicks` while remaining SQLite-compatible.
- Extended API DI tests to cover PostgreSQL provider selection and missing PostgreSQL connection-string validation.
- Added an optional Testcontainers-backed PostgreSQL integration test proving the Operations app service can raise and query alarms across scopes when `OPENLINEOPS_RUN_POSTGRES_INTEGRATION=1` is set.

Delivered on 2026-06-30 (CAP EF transaction PostgreSQL integration coverage slice):

- Extended the EventBus PostgreSQL profile so `UseInMemory=false` with `RabbitMq.Enabled=false` uses CAP's in-memory message queue while still storing outbox rows in PostgreSQL. This supports local and integration-test profiles without requiring RabbitMQ.
- Added optional Testcontainers coverage for the CAP EF transaction coordinator with Operations PostgreSQL persistence and CAP PostgreSQL storage in the same database.
- The test registers `AddOpenLineOpsOperationsModule()` and `AddOpenLineOpsEventBus()` together, enables `OpenLineOps:EventBus:EnableEfCoreTransactionCoordinator`, raises an alarm through `IAlarmAppService`, and verifies both the `operations_alarms` row and `cap.published` outbox record.
- Documented the PostgreSQL storage plus in-memory queue profile in `docs/eventbus-transaction-coordination.md`; RabbitMQ remains the deployment transport for cross-process messaging.

Delivered on 2026-06-30 (Operations PostgreSQL deployment documentation slice):

- Added `docs/operations-postgresql-deployment.md` covering the server profile for Operations PostgreSQL persistence, CAP PostgreSQL outbox storage, RabbitMQ transport, PostgreSQL integration-test mode, environment variable overrides, migrations, and operational rules.
- Linked the deployment guide from the README documentation index and from `docs/eventbus-transaction-coordination.md`.
- Added EventBus configuration fail-fast validation so `UseInMemory=false` requires the named CAP PostgreSQL connection string, and `EnableEfCoreTransactionCoordinator=true` is rejected while CAP storage is still in-memory.
- Added EventBus DI tests covering missing PostgreSQL connection string validation, invalid in-memory transaction-coordinator configuration, and successful transaction-coordinator registration for PostgreSQL CAP storage.

Delivered on 2026-06-30 (production readiness health checks slice):

- Added API readiness health-check registration for Operations PostgreSQL persistence and EventBus CAP PostgreSQL storage.
- The default local profile still exposes `/health/ready` without external dependency probes, so SQLite/in-memory desktop development remains lightweight.
- PostgreSQL server profiles register `openlineops.operations.postgresql` and `openlineops.eventbus.postgresql` readiness checks, each opening the configured connection and executing `SELECT 1`.
- Added RabbitMQ transport readiness registration for deployment profiles where `OpenLineOps:EventBus:RabbitMq:Enabled` is `true`; the check opens and closes an AMQP connection without declaring broker topology.
- Added a direct API package reference to `RabbitMQ.Client` so the host owns its readiness probe dependency explicitly instead of relying on CAP's transitive package graph.
- Added API tests covering the default no-external-check profile, PostgreSQL server-profile readiness check registration, RabbitMQ transport readiness registration, and `/health/ready` success for the default local profile.
- Documented readiness behavior in `docs/operations-postgresql-deployment.md`.

Delivered on 2026-07-09 (RabbitMQ container readiness integration slice):

- Added `Testcontainers.RabbitMq` to the optional container integration test project.
- Added `OPENLINEOPS_RUN_RABBITMQ_INTEGRATION=1` as a separate opt-in from the PostgreSQL container tests.
- Added a Docker-backed RabbitMQ readiness integration test that starts a RabbitMQ broker, binds the broker settings through `OpenLineOps:EventBus:RabbitMq`, and verifies the `openlineops.eventbus.rabbitmq` readiness check returns healthy after opening a real AMQP connection.
- Added a direct `Microsoft.OpenApi` package reference to the API project and pinned it through central package management so the OpenAPI transitive dependency stays on a non-vulnerable version.

### Milestone 8: Packaging And Open Source Release

Status: in progress.

Goal: make the project usable by external developers.

Deliverables:

- GitHub repository setup.
- README with positioning, architecture, quick start, and roadmap.
- License.
- Contribution guide.
- Code of conduct if desired.
- Issue templates.
- Plugin authoring guide.
- Example plugin.
- CI build/test workflow.
- Signed release packaging strategy.

Exit criteria:

- A new contributor can clone, restore, build, test, run the API, and understand where to add a module or plugin.

Delivered on 2026-06-29:

- Added root `README.md` with product positioning, repository map, prerequisites, quick start, verification commands, architecture rules, and documentation links.
- Added root `.gitignore` for .NET, Electron, local runtime, coverage, and IDE outputs.
- Added `LICENSE` using MIT License text for the initial open-source direction.
- Added `CONTRIBUTING.md` with setup, architecture expectations, pull request checklist, testing rules, and coding style notes.
- Added `CODE_OF_CONDUCT.md` and `SECURITY.md` for public repository readiness.
- Added GitHub Actions workflow `.github/workflows/build.yml` covering .NET restore/format/build/test/vulnerability check, sample plugin build, desktop install/typecheck/build/audit.
- Added pull request template and issue templates for bug reports, feature requests, and plugin requests.
- Added `docs/plugin-authoring.md` documenting plugin contracts, manifest fields, device/process command plugins, isolation modes, packaging layout, and review checklist.
- Added `docs/python-scripting-integration.md` documenting the required `lib/pythonscript` integration, Blockly-first editing rule, manual Python editing mode, script node model, validation flow, runtime flow, and .NET 10 compatibility work.
- Added `docs/release-packaging.md` with first release goals, versioning, artifacts, signing, CI gates, and release checklist.
- Added `samples/plugins/OpenLineOps.SamplePlugins.LoopbackDevice` as a compilable example device command plugin.
- Added the sample plugin project to `OpenLineOps.sln` and `OpenLineOps.slnx`.
- Added Electron smoke test to the GitHub Actions workflow after desktop build.

Delivered on 2026-06-30 (release manifest tooling slice):

- Added `tools/OpenLineOps.ReleaseManifest`, a .NET 10 CLI for release artifact metadata generation.
- The tool scans an artifact directory recursively, excludes stale manifest/checksum/release-note output files, sorts artifact paths deterministically, and writes `release-manifest.json`, `checksums.sha256`, and an optional release notes starter.
- Added `tests/OpenLineOps.ReleaseManifest.Tests` covering manifest JSON, SHA-256 checksum lines, release notes generation, output-file exclusion, empty artifact directory failure, and command validation.
- Added the release manifest tool and tests to `OpenLineOps.sln` and `OpenLineOps.slnx`.
- Added the release manifest help smoke command to the README verification commands.
- Updated `.github/workflows/build.yml` to smoke-test the release manifest tool and to use the same `lib/pythonscript` plus `lib/NetDevPack` format excludes as local verification.

Delivered on 2026-07-09 (release artifact kind gate slice):

- Extended the .NET 10 release manifest tool to infer an artifact `kind` from the staged release directory or root artifact file prefix.
- Release manifest schema now records artifact kind alongside relative path, file name, size, and SHA-256.
- Added repeatable `--require-kind <kind>` command-line validation so release CI can fail fast if required package categories such as `source`, `api`, `desktop`, `plugin-host`, `script-worker`, or `sample-plugin` are missing.
- Updated release notes generation to include artifact kind in the artifact table.
- Documented the recommended release staging layout and required-kind gate in `docs/release-packaging.md`.

Delivered on 2026-07-09 (release artifact gate CI slice):

- Added `eng/verify-release-artifact-gate.ps1` to stage a representative `source` entry plus representative build outputs for `api`, `desktop`, `plugin-host`, `script-worker`, and `sample-plugin` artifact kinds from prior .NET and desktop build steps.
- The script runs the .NET 10 release manifest tool with repeatable `--require-kind` arguments, verifies manifest schema version 2, and fails if any required artifact kind is missing.
- Added the release artifact kind gate to `.github/workflows/build.yml` after desktop production build and before the Electron smoke test.
- Added the local release artifact gate command to the README desktop verification flow and `docs/release-packaging.md`.

Delivered on 2026-07-09 (local release staging slice):

- Added `eng/stage-release-artifacts.ps1` for local release candidate staging.
- The script runs `dotnet publish` for the API host, plugin host, script worker, and loopback sample plugin; runs the desktop production build; creates zip artifacts under `artifacts/release`; and generates manifest, checksum, and release-note files through the .NET 10 release manifest tool.
- Release staging enforces the required artifact kinds `source`, `api`, `desktop`, `plugin-host`, `script-worker`, and `sample-plugin` against the generated zip artifacts.
- Release staging now generates a source archive while excluding generated output, build products, dependency caches, runtime data, and local environment files.
- Desktop staging currently emits an unsigned `win-unpacked` development package plus `dist`, `dist-electron`, and desktop metadata, leaving signed installer/portable packaging as the remaining production desktop packaging step.
- Updated the file-system plugin package catalog to accept `manifest.json` by default so the documented sample plugin package layout and shipped sample manifest are discoverable without custom catalog configuration.
- Updated `docs/release-packaging.md` and `docs/plugin-authoring.md` with the staging command and supported manifest file names.

Delivered on 2026-07-09 (CI release staging slice):

- Replaced the CI representative artifact-kind gate with the full `eng/stage-release-artifacts.ps1` release staging flow.
- CI now publishes Release API, plugin host, script worker, and sample plugin outputs; packages the existing desktop production build; creates the source archive; and validates all required release artifact kinds through the release manifest tool.
- Kept `eng/verify-release-artifact-gate.ps1` as a faster local/diagnostic gate for checking built outputs without generating full release candidate archives.

Delivered on 2026-07-09 (release manifest verification slice):

- Added `--verify` mode to `tools/OpenLineOps.ReleaseManifest` for validating an existing release candidate without regenerating manifest, checksum, or release-note files.
- Verify mode checks artifact path confinement, file existence, manifest file names, inferred artifact kinds, sizes, SHA-256 hashes, required artifact kinds, and optional checksum-file parity.
- Added release manifest tests for generated release verification, tampered artifact hash detection, and CLI verify-mode execution.
- Documented the verify command in `docs/release-packaging.md`, the README verification flow, and the current verification command list.

Delivered on 2026-07-09 (CI release artifact upload slice):

- Added an explicit CI `Verify staged release manifest` step after full release staging so GitHub Actions validates the staged `release-manifest.json` and `checksums.sha256` before later desktop gates run.
- Added an `actions/upload-artifact` step after desktop smoke and high-severity audit so a passing CI run publishes the validated `artifacts/release` directory as `openlineops-release-<run-number>`.
- Configured the artifact upload to fail when release files are missing, retain artifacts for 14 days, and disable compression because the staged release packages are already zip archives.
- Updated `docs/release-packaging.md` to document the CI artifact upload flow and release checklist inspection step.

Delivered on 2026-07-09 (publication readiness gate slice):

- Added `eng/verify-publication-readiness.ps1`, a pre-publication gate for required open-source governance files, .NET 10 SDK pinning, GitHub issue and pull-request templates, CI release staging/upload wiring, MIT license presence, plugin/Python scripting documentation, and staged release manifest/checksum verification.
- The gate runs strict by default and fails while final release-only items are still pending, including unsigned desktop package evidence.
- Added `-AllowPendingExternal` so the current local development stage can keep validating non-external release readiness while explicitly warning about the remaining external publication blockers.
- Documented the readiness gate in the README verification flow, `docs/release-packaging.md`, and the current verification command list.

Delivered on 2026-07-09 (desktop unpacked package slice):

- Added `apps/desktop/scripts/package-win-unpacked.mjs` and desktop `package:win` / `package:win:ci` commands to create an unsigned Windows unpacked Electron development package from the built renderer, main, preload, and Electron runtime files.
- Release staging now runs the desktop package command and includes `package/win-unpacked/OpenLineOps.exe` plus package notes inside `desktop-openlineops-<version>.zip`.
- The source archive excludes generated desktop `release` output while keeping the package script itself.
- Publication readiness now checks that the desktop release archive contains the expected unpacked executable and package notes.

Delivered on 2026-07-09 (CI publication readiness gate slice):

- Added a GitHub Actions `Verify publication readiness` step after staged release manifest verification.
- The CI step runs `eng/verify-publication-readiness.ps1 -AllowPendingExternal`, so CI continuously validates open-source governance files, CI release wiring, manifest/checksum parity, and desktop package archive structure while keeping signed desktop release productionization as an explicit warning until code signing is complete.
- Updated README and `docs/release-packaging.md` to describe the CI readiness gate and current staged desktop package flow.

Delivered on 2026-07-09 (publication metadata finalization slice):

- Added `eng/finalize-publication-metadata.ps1` to apply the final public GitHub repository URL, private security contact, and private conduct contact once those external values are available.
- The script updates `SECURITY.md`, `CODE_OF_CONDUCT.md`, and `.github/ISSUE_TEMPLATE/config.yml`, then runs the strict publication readiness gate by default so outdated reporting text cannot remain unnoticed.
- Added script presence and required contact-parameter checks to `eng/verify-publication-readiness.ps1`.
- Documented the finalization command in `docs/release-packaging.md`.

Delivered on 2026-07-09 (publication metadata finalization verification slice):

- Added `eng/verify-publication-metadata-finalization.ps1` to run repeatable failure-path and success-path checks for publication metadata finalization without mutating the repository root files.
- The verification rejects non-HTTPS repository URLs, extra GitHub URL path segments, and placeholder security contacts, then generates final metadata under `output/` and verifies the expected contacts, security-policy URL, and absence of pre-publication wording.
- Added the verification to GitHub Actions before the publication readiness gate and included it in the README and release packaging verification flow.

Delivered on 2026-07-09 (desktop signing readiness slice):

- Added `eng/sign-desktop-package.ps1` for Windows Electron package signing through `signtool.exe`, with thumbprint, PFX, or certificate auto-selection modes, timestamping, post-sign verification, and `-PlanOnly` support.
- Release staging now exposes `-SignDesktopPackage` and code-signing parameters so production desktop signing can happen before zip archive, checksum, and release manifest generation.
- Added `eng/verify-desktop-signing-readiness.ps1` to verify certificate-selector validation and deterministic `.exe` / `.dll` / `.node` signing plans without requiring a real signing certificate.
- Added the signing readiness verification to GitHub Actions, publication readiness checks, README verification commands, and release packaging documentation.

Delivered on 2026-07-09 (release candidate inspection slice):

- Added `eng/inspect-release-candidate.ps1` for inspecting staged release artifacts or a downloaded CI `openlineops-release-<run-number>` artifact.
- The inspection reuses release manifest verification, checks release-note coverage, validates expected zip entries for source, API, desktop, plugin-host, script-worker, and sample-plugin artifacts, and can enforce a valid Authenticode signature on the desktop executable through `-RequireSignedDesktop`.
- Added the release candidate inspection to GitHub Actions before artifact upload, publication readiness checks, README verification commands, and release packaging documentation.

Delivered on 2026-07-09 (open-source metadata verification slice):

- Added default OpenLineOps package metadata to `Directory.Build.props`, including product, authors, company, MIT license expression, no license-acceptance requirement, and git repository type.
- Added MIT license metadata to the Electron desktop `package.json` and root package-lock entry so the packaged desktop shell carries explicit license metadata.
- Added `eng/verify-open-source-metadata.ps1` and wired it into GitHub Actions, publication readiness checks, README verification commands, release packaging documentation, and release candidate source-archive inspection.

Delivered on 2026-07-09 (third-party license metadata verification slice):

- Added `eng/verify-third-party-license-metadata.ps1` to inspect `OpenLineOps.sln` NuGet restore assets plus local package `.nuspec` metadata and the Electron desktop `package-lock.json`.
- The verification fails when dependency license metadata is missing and blocks license values that require release review, including GPL, LGPL, AGPL, and SSPL patterns.
- Wired the third-party license metadata gate into GitHub Actions, publication readiness checks, README verification commands, release packaging documentation, and release candidate source-archive inspection.

Delivered on 2026-07-09 (third-party notice generation slice):

- Added generated root `THIRD-PARTY-NOTICES.md` from `OpenLineOps.sln` NuGet restore metadata and the Electron desktop package lock.
- Extended `eng/verify-third-party-license-metadata.ps1` with `-UpdateNotice` so dependency changes can regenerate the notice, while default verification fails if the committed notice is missing or stale.
- Wired the generated notice into publication readiness checks, release packaging documentation, README guidance, and release candidate source-archive inspection.

Delivered on 2026-07-09 (production desktop signature publication gate slice):

- Extended `eng/verify-publication-readiness.ps1` to extract `OpenLineOps.exe` from the staged desktop archive and require a valid Authenticode signature in strict mode.
- Kept `-AllowPendingExternal` usable for local and early CI readiness by reporting the unsigned desktop archive as a pending external warning until a real code-signing certificate is available.
- Updated release packaging documentation so public release readiness requires finalized repository metadata, reporting contacts, and signed desktop release artifacts.

Delivered on 2026-07-09 (release archive safety inspection slice):

- Extended `eng/inspect-release-candidate.ps1` to reject unsafe zip entry names across every staged release archive.
- The inspection now fails on empty entry names, NUL characters, absolute paths, `.` or `..` path segments, duplicate normalized entries, and paths that would escape the intended extraction root.
- Added publication readiness self-check coverage so the release gate verifies the archive safety inspection remains wired.

Delivered on 2026-07-09 (source archive sensitive-file guard slice):

- Extended `eng/stage-release-artifacts.ps1` to exclude common local secret and key file names from source archive staging, including non-example `.env` files, `.npmrc`, `.netrc`, `.pfx`, `.p12`, `.key`, `.snk`, publish settings, SSH identity names, and sensitive PEM names.
- Extended `eng/inspect-release-candidate.ps1` to fail if the staged source archive contains those sensitive entries, even when the archive did not come from the local staging script.
- Added publication readiness self-check coverage and release packaging documentation for the source archive sensitive-file guard.

Delivered on 2026-07-09 (release provenance metadata slice):

- Extended `eng/stage-release-artifacts.ps1` to generate `release-provenance.json` after release manifest generation.
- The provenance file records selected tool versions, build switches, optional Git context, metadata-file hashes, and a manifest-aligned artifact inventory without storing local absolute paths.
- Extended `eng/inspect-release-candidate.ps1` to require provenance metadata, verify product/version parity with the manifest, validate metadata-file hashes, require tool versions, and compare each provenance artifact entry with the release manifest.
- Added publication readiness self-check coverage plus README and release packaging documentation for provenance metadata.

Delivered on 2026-07-09 (release candidate inspection verification slice):

- Added `eng/verify-release-candidate-inspection.ps1` to generate minimal release candidates and prove `eng/inspect-release-candidate.ps1` accepts a valid candidate while rejecting unsafe zip paths, sensitive source archive entries, bad provenance metadata, and missing provenance metadata.
- Added the verification script to GitHub Actions after staged candidate inspection so inspector behavior is tested continuously, not only documented through ad hoc local fixtures.
- Extended publication readiness self-checks and source archive inspection to require the verification script, keeping the open-source release package self-verifying.

Delivered on 2026-07-09 (publication evidence reporting slice):

- Added `eng/write-publication-evidence.ps1` to generate `publication-evidence.json` and `publication-evidence.md` from the current release candidate, publication readiness gates, release candidate inspection gates, signing readiness, metadata finalization verification, license confirmation input, and GitHub Actions run URL input.
- The evidence script passes in local development while recording pending external items, and supports `-RequirePublishable` for the final release assertion after MIT confirmation, finalized repository metadata, signed desktop artifacts, and GitHub-hosted Windows CI proof are available.
- Added the evidence script to GitHub Actions and the uploaded release artifact set, and made source archive inspection require the script so release candidates carry their own publication audit path.

Delivered on 2026-07-09 (publication evidence verification slice):

- Added `eng/verify-publication-evidence.ps1` to verify publication evidence generation, MIT confirmation recording, GitHub Actions run URL recording, invalid GitHub Actions URL rejection, and `-RequirePublishable` behavior.
- Added the verification to GitHub Actions, publication readiness self-checks, README verification commands, release packaging documentation, and source archive inspection.

Delivered on 2026-07-09 (final publication preflight slice):

- Added `eng/prepare-final-publication.ps1` as the final publication orchestration entry point for applying repository metadata, staging signed artifacts, inspecting the signed desktop candidate, running strict publication readiness, and requiring publishable evidence.
- Added `eng/verify-final-publication-preflight.ps1` to cover missing MIT confirmation, invalid GitHub Actions run URLs, missing code-signing selectors, and `-PlanOnly` command-chain output without mutating publication metadata or requiring a real signing certificate.
- Added the preflight verification to GitHub Actions, publication readiness self-checks, README verification commands, release packaging documentation, uploaded diagnostic artifacts, and source archive inspection.

Delivered on 2026-07-09 (publication evidence work-directory isolation slice):

- Updated `eng/write-publication-evidence.ps1` so every child gate that writes temporary files receives an evidence-run-specific `-WorkRoot` under the evidence output directory.
- This prevents concurrent local evidence runs from sharing default `output/` folders for release candidate inspection fixtures, desktop signing readiness fixtures, publication metadata finalization fixtures, and strict readiness extraction.
- Added publication readiness self-check coverage for the child work-directory isolation.

Delivered on 2026-07-09 (final publication preflight evidence slice):

- Updated `eng/verify-final-publication-preflight.ps1` to write `publication-preflight.json` and `publication-preflight.md` under `output/final-publication-preflight`.
- This makes the CI-uploaded final publication preflight diagnostics path deterministic instead of depending on an empty output directory.
- Added publication readiness self-check coverage for the preflight evidence files.

Delivered on 2026-07-09 (CI workflow action reference verification slice):

- Added `eng/verify-ci-workflow-actions.ps1` to verify the GitHub Actions workflow uses only approved, version-pinned action references.
- The verifier requires `actions/upload-artifact@v7` and checks the release artifact upload contract, including `artifacts/release`, publication evidence diagnostics, final publication preflight diagnostics, missing-file failure, 14-day retention, and disabled redundant compression.
- Added the verifier to GitHub Actions immediately after checkout, publication readiness self-checks, README verification commands, release packaging documentation, and release candidate source-archive inspection.

Delivered on 2026-07-09 (CI release artifact bundle inspection slice):

- Added `eng/inspect-ci-release-artifact.ps1` to inspect the exact directory shape uploaded by GitHub Actions as `openlineops-release-<run-number>`.
- The inspector verifies `artifacts/release`, publication evidence, publication evidence verification diagnostics, final publication preflight diagnostics, manifest/evidence version parity, required artifact kinds, evidence gate statuses, preflight cases, and release candidate archive inspection.
- Added `-RequirePublishable` so the downloaded CI artifact can be used for the final release assertion after MIT confirmation, GitHub-hosted Windows CI proof, and signed desktop artifacts are available.
- Wired the inspector into GitHub Actions before artifact upload, publication readiness self-checks, README verification commands, release packaging documentation, release checklist guidance, and release candidate source-archive inspection.

Delivered on 2026-07-09 (release dependency inventory metadata slice):

- Extended `eng/verify-third-party-license-metadata.ps1` with dependency inventory generation and verification from the same NuGet restore metadata and desktop `package-lock.json` sources used by `THIRD-PARTY-NOTICES.md`.
- Release staging now writes `release-dependency-inventory.json` under `artifacts/release` after manifest/checksum generation and records its SHA-256 hash in `release-provenance.json`.
- Release candidate inspection now requires the dependency inventory, verifies schema/product/version/count/package/license metadata, rejects review-blocked license patterns, and verifies the dependency inventory provenance hash.
- Release candidate inspection verification now covers missing and bad dependency inventory fixtures.

Delivered on 2026-07-09 (release metadata checksum slice):

- Release staging now writes `release-metadata-checksums.sha256` after provenance generation, covering `release-manifest.json`, `checksums.sha256`, `release-notes.md`, `release-dependency-inventory.json`, and `release-provenance.json`.
- Release candidate inspection now requires and verifies the metadata checksum file so metadata tampering is caught independently of artifact checksum validation.
- Release candidate inspection verification now covers missing and bad metadata checksum fixtures.
- Publication readiness and CI release artifact bundle inspection now require the metadata checksum file.

Delivered on 2026-07-09 (CI release artifact inspection evidence slice):

- Updated `eng/inspect-ci-release-artifact.ps1` to write `ci-release-artifact-inspection.json` and `.md` under `output/ci-release-artifact-inspection` for both passing and failing bundle inspections.
- The report records inspection status, bundle root, release version, artifact kinds, dependency counts, metadata checksum entry count, publication evidence publishability, final preflight cases, and failures.
- Added the inspection diagnostics directory to the GitHub Actions release artifact upload and to workflow/readiness self-checks.

Delivered on 2026-07-09 (GitHub Actions repository verification slice):

- Verified the public repository `main` workflow after repository initialization with GitHub Actions run `29011973592`.
- The run completed successfully for commit `2d08e4e83393033ca8862309f1101aef460602a1` on the `windows-latest` hosted runner.
- The executed job proved restore, format, build, .NET tests, PythonScript component build, package vulnerability check, open-source metadata verification, third-party license metadata verification, release staging, release manifest verification, release candidate inspection, publication readiness with `-AllowPendingExternal`, publication evidence verification, final publication preflight verification, CI release artifact bundle inspection, Electron desktop smoke, desktop audit, and release artifact upload.
- The Electron smoke step succeeded on GitHub-hosted Windows CI after the public repository was created, covering the remaining CI proof required for the current open-source release milestone.

Remaining Milestone 8 work:

- Decide whether MIT remains the final license before publishing the public repository.
- Provision a real code-signing certificate and produce a signed production Electron installer or portable artifact that passes the strict publication readiness signature gate.

### Milestone 9: Automation Project Workspace And Site Layout

Status: in progress.

Goal: make the product's primary workflow match the project-oriented automation workbench model.

Deliverables:

- `docs/automation-project-workspace.md` target architecture guide.
- `docs/composable-automation-model.md` detailed building block model.
- ADR for making Automation Project Workspace the primary product shell.
- Projects bounded context with Domain/Application/Infrastructure/API split.
- `AutomationProject` aggregate with project manifest, lifecycle, and strong typed ids.
- Topology model for project applications, equipment nodes, automation modules, capability contracts, driver bindings, slot groups, and slots.
- `SiteLayout` draft model with top-down coordinates, layers, layout elements, target references, and future 3D metadata extension points.
- Project publication workflow that creates immutable `PublishedProjectSnapshot` records.
- API endpoints for project create/list/open/update, composition updates, layout updates, and publish.
- In-memory and SQLite persistence adapters for local desktop development.
- Host-level API tests and domain behavior tests for project lifecycle, composition invariants, layout validation, and snapshot immutability.
- Electron start window for new/open/recent projects.
- Project explorer sidebar scoped to the active project.
- First top-down site layout editor for equipment nodes, modules, slot groups, slots, devices, labels, and zones.
- Runtime launch update so the UI starts sessions from a published project snapshot instead of manually assembled ids.
- Traceability update so traces include project snapshot, topology, layout, equipment node, module, slot group, and slot references.

Delivered on 2026-07-09 (automation project workspace domain skeleton slice):

- Added the source-neutral composable automation model that maps project, application, system, driver, group, slot, and site layout language into DDD concepts.
- Added `modules/OpenLineOps.Projects.Domain` with `AutomationProject`, `ProjectApplication`, `PublishedProjectSnapshot`, project-local strong typed ids, snapshot publication validation, and a domain event for snapshot publication.
- Added `modules/OpenLineOps.Topology.Domain` with `AutomationTopology`, `EquipmentNode`, `AutomationModule`, `CapabilityContract`, `DriverBinding`, `SlotGroup`, `SlotDefinition`, and `SiteLayout`.
- Added topology invariants for root node uniqueness, parent existence, allowed node child kinds, declared capability references, capability binding uniqueness, slot address uniqueness, and slot group capacity.
- Added project invariants for duplicate application rejection, linked topology/process checks, required capability bindings, required runtime targets, active snapshot assignment, and snapshot publication domain-event emission.
- Added `tests/OpenLineOps.Projects.Tests` and `tests/OpenLineOps.Topology.Tests` behavior coverage for the first project/topology model.

Delivered on 2026-07-09 (automation project workspace application foundation slice):

- Added `modules/OpenLineOps.Projects.Application` with project workspace use cases for create/list/get automation project, add application, link topology, link process definition, and publish project snapshot.
- Added `modules/OpenLineOps.Projects.Infrastructure` with an in-memory automation project repository behind the application repository port.
- Added `modules/OpenLineOps.Topology.Application` with topology composition use cases for equipment nodes, capabilities, modules, driver bindings, slot groups, slots, site layout creation, and layout element placement.
- Added `modules/OpenLineOps.Topology.Infrastructure` with in-memory topology and site layout repositories behind application repository ports.
- Added application-layer behavior coverage proving project snapshot publication can be driven through the service layer and topology layout elements can only reference existing topology targets.

Delivered on 2026-07-09 (automation project workspace API foundation slice):

- Added `modules/OpenLineOps.Projects.Api` with host-routed HTTP endpoints for automation project create/list/get, application creation, topology linking, process-definition linking, and project snapshot publication.
- Added `modules/OpenLineOps.Topology.Api` with host-routed HTTP endpoints for automation topology create/list/get, equipment node, capability, module, driver binding, slot group, slot, site layout, and layout element composition.
- Registered the Projects and Topology API modules in the ASP.NET Core host and solution files so Electron can call the same backend boundary as other workbenches.
- Added host-level API coverage proving the first project workspace can be composed through HTTP, including topology blocks, layout target validation, project application linking, and snapshot publication.

Delivered on 2026-07-09 (automation project workspace manifest slice):

- Added the first monolithic project-folder manifest prototype; this prototype
  was removed by the 2026-07-10 portable Application clean cutover and is not an
  accepted input format.
- Added controlled domain restore factories for `AutomationProject`, `ProjectApplication`, and `PublishedProjectSnapshot` so infrastructure can rehydrate project aggregates without raising new domain events.
- Added `AutomationProjectWorkspaceService` with create/open/save manifest use cases and a file-system manifest store that writes manifests through a temporary file replacement.
- Added `/api/automation-project-workspaces`, `/api/automation-project-workspaces/open`, and `PUT /api/automation-projects/{projectId}/manifest` so Electron can create, open, and save project folders through the backend boundary.
- Added application-service and host-level API tests proving project manifests round-trip through a fresh repository and through the ASP.NET Core host.

Delivered on 2026-07-10 (portable Application project clean cutover):

- Replaced the monolithic manifest prototype with one root `<projectId>.oloproj`
  plus one independent `.oloapp` inside every Application directory.
- Moved Topology, SiteLayout, Process/Blockly/Python, custom-block, and
  Engineering configuration source below the owning Application root and
  removed host `ProjectId` from every Application-local document.
- Added copy-and-import composition: a complete Application directory can move
  from Project A to Project B without rewriting its contents.
- Removed obsolete project/resource DTOs, fallback paths, readers, recent-list
  migration, and Runner support. Only exact current schemas are accepted; this
  is an intentional breaking cutover with no compatibility path.
- Added strict unknown-field, path-containment, duplicate-id/path,
  reparse-point, immutable-release, and cross-project portability coverage.

Exit criteria:

- A user can create or open an automation project from Electron.
- A user can define at least one application, equipment node, automation module, slot group, slot, capability contract, and driver binding inside that project.
- A user can place those elements on a top-down layout and persist the layout.
- A user can connect a Blockly/PythonScript process to a project target.
- A user can publish the project and start a runtime session from the published project snapshot.
- Trace output identifies the project snapshot and target equipment node, slot group, or slot used by the run.
- Existing Process, Device, Runtime, and Traceability tests remain green.

## Testing Strategy

Testing layers:

- Domain unit tests: aggregates, value objects, policies, state transitions.
- Application tests: use cases, validation, authorization decisions, idempotency.
- Integration tests: persistence, plugin lifecycle, API host, background workers.
- Optional container integration tests: PostgreSQL persistence adapters and RabbitMQ EventBus transport readiness with Testcontainers, excluded from default solution tests and enabled explicitly through `OPENLINEOPS_RUN_POSTGRES_INTEGRATION=1` or `OPENLINEOPS_RUN_RABBITMQ_INTEGRATION=1`.
- Contract tests: API DTO compatibility, plugin manifest compatibility.
- End-to-end tests: Electron to API to runtime simulation.

Minimum rules:

- Every aggregate gets behavior tests before infrastructure.
- Every runtime transition gets positive and negative tests.
- Every public API added for the UI gets at least one host-level integration test.
- Plugin contracts require compatibility tests because third-party extensions depend on them.

## API Strategy

Use HTTP APIs for configuration, traceability, and management. Use SignalR for live runtime monitoring. Consider gRPC only for high-frequency local service communication after there is evidence HTTP/SignalR is insufficient.

Initial API groups:

- `/api/platform`
- `/api/automation-projects`
- `/api/automation-topologies`
- `/api/site-layouts`
- `/api/workspaces`
- `/api/projects`
- `/api/recipes`
- `/api/stations`
- `/api/devices`
- `/api/process-definitions`
- `/api/runtime-sessions`
- `/api/traces`
- `/api/plugins`
- `/health/live`
- `/health/ready`

## Persistence Strategy

Start with relational persistence for core transactional data. Keep storage behind application ports until the first bounded context is stable.

Recommended direction:

- PostgreSQL for server/deployment mode.
- SQLite for local desktop development or single-station mode if product requirements demand it.
- File/object storage abstraction for artifacts.
- Event/outbox table for integration and realtime projection reliability.
- Store early aggregate snapshots as JSON documents behind repository ports, with relational columns for identities, status, timestamps, and later query projections. This avoids leaking ORM mapping concerns into rich DDD aggregates while bounded-context behavior is still evolving.

Do not decide final storage solely from UI convenience. Runtime traceability and audit requirements should drive the persistence model.

## Immediate Next Development Tasks

1. Add local desktop persistence for automation projects and topology drafts, starting with SQLite repository adapters behind the existing application ports.
2. Define the first project folder/package manifest so Electron can create and open project directories consistently.
3. Move Electron toward the new/open/recent project shell, active project context, and project explorer.
4. Connect the process editor to project targets so Blockly/PythonScript nodes can reference equipment nodes, modules, slot groups, slots, and capability contracts.
5. Add runtime launch from published project snapshots and enrich trace records with project, topology, layout, slot, and capability binding references.

## Current Verification Commands

Run from the repository root:

```powershell
dotnet restore OpenLineOps.sln
dotnet format OpenLineOps.sln whitespace --no-restore --verify-no-changes --exclude lib/pythonscript --exclude lib/NetDevPack --verbosity minimal
dotnet format OpenLineOps.sln style --no-restore --verify-no-changes --exclude lib/pythonscript --exclude lib/NetDevPack --severity warn --verbosity minimal
dotnet build OpenLineOps.sln --no-restore
dotnet test OpenLineOps.sln --no-build
dotnet build lib/pythonscript/PythonScript.sln --no-restore
dotnet build OpenLineOps.slnx --no-restore
dotnet test OpenLineOps.slnx --no-build
dotnet list OpenLineOps.sln package --vulnerable --include-transitive
dotnet run --project tools/OpenLineOps.ReleaseManifest/OpenLineOps.ReleaseManifest.csproj -- --help
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-open-source-metadata.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-third-party-license-metadata.ps1 -UpdateNotice
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-third-party-license-metadata.ps1
Set-Location apps/desktop
npm install
npm run typecheck
npm run build
npm run package:win:ci
Set-Location ..\..
powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify-release-artifact-gate.ps1 -Configuration Debug -Version 0.0.0-local
powershell -NoProfile -ExecutionPolicy Bypass -File eng/stage-release-artifacts.ps1 -Configuration Release -Version 0.0.0-local -NoRestore
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
Set-Location apps/desktop
npm run smoke:e2e
npm audit --audit-level=high --registry=https://registry.npmjs.org
```

Optional PostgreSQL and RabbitMQ container integration profiles:

```powershell
dotnet restore tests/OpenLineOps.PostgresIntegration.Tests/OpenLineOps.PostgresIntegration.Tests.csproj
dotnet build tests/OpenLineOps.PostgresIntegration.Tests/OpenLineOps.PostgresIntegration.Tests.csproj --no-restore
dotnet test tests/OpenLineOps.PostgresIntegration.Tests/OpenLineOps.PostgresIntegration.Tests.csproj --no-build
$env:OPENLINEOPS_RUN_POSTGRES_INTEGRATION = "1"
dotnet test tests/OpenLineOps.PostgresIntegration.Tests/OpenLineOps.PostgresIntegration.Tests.csproj --no-build
Remove-Item Env:\OPENLINEOPS_RUN_POSTGRES_INTEGRATION
$env:OPENLINEOPS_RUN_RABBITMQ_INTEGRATION = "1"
dotnet test tests/OpenLineOps.PostgresIntegration.Tests/OpenLineOps.PostgresIntegration.Tests.csproj --no-build
```

The opt-in commands require a working local Docker environment. Without the matching environment variable, the optional container tests are discovered and skipped.

Current result on 2026-06-29:

- Restore: passed.
- Whitespace format verification: passed.
- Style warning format verification: passed.
- `OpenLineOps.sln` build: passed with zero warnings.
- `OpenLineOps.sln` tests: passed, 220 total.
- `OpenLineOps.slnx` build: passed with zero warnings.
- `OpenLineOps.slnx` tests: passed, 220 total with `dotnet test OpenLineOps.slnx --no-restore -m:1 -v minimal`.
- Sample plugin build: passed with zero warnings.
- Desktop `apps/desktop` install: passed after retrying Electron binary download with `ELECTRON_MIRROR=https://npmmirror.com/mirrors/electron/`.
- Desktop Electron version: upgraded to `^42.5.1` after high-severity Electron advisories were reported for the earlier range.
- Desktop `npm run typecheck`: passed.
- Desktop `npm run build`: passed.
- Desktop `npm run smoke:e2e`: passed on 2026-06-30, including Electron startup, backend startup, SignalR events, simulated runtime completion, trace row output, Trace workbench search/detail/export, adding a Command node in the process graph editor, Blockly-default PythonScript process create/publish, engineering snapshot creation, published process runtime launch from the UI, Engineering workbench snapshot publication from the UI, and Devices workbench registration/connection.
- Desktop `npm audit --audit-level=high --registry=https://registry.npmjs.org`: passed, zero vulnerabilities reported.
- Desktop renderer preview: passed with HTTP 200 from `npx vite preview --host 127.0.0.1 --port 4173`.
- Desktop visual verification: passed through system Chrome headless screenshot at 1440x900, reviewed at `output/playwright/desktop-dashboard.png`.
- Playwright browser install: not completed in this workspace because the browser download hung; system Chrome was used for the visual verification instead.
- `OpenLineOps.slnx` default parallel test run displayed all test assemblies passing but returned exit code 1 twice in this workspace; use the serial command above until the SDK/test runner behavior is isolated.
- Optional PostgreSQL integration project restore: passed.
- Optional PostgreSQL integration project build: passed with zero warnings.
- Optional PostgreSQL integration project default test run: passed with 4 skipped because `OPENLINEOPS_RUN_POSTGRES_INTEGRATION` was not set.
- Optional PostgreSQL container execution: not run in the current workspace because `docker` CLI is not installed.
- Vulnerable package check: passed for the default solution and optional PostgreSQL integration project, no vulnerable packages reported from current configured sources.

Current result on 2026-06-30:

- Whitespace format verification excluding `lib/pythonscript` and `lib/NetDevPack`: passed.
- Style warning format verification excluding `lib/pythonscript` and `lib/NetDevPack`: passed.
- `OpenLineOps.sln` restore: passed after wiring `shared/OpenLineOps.Domain.Abstractions` to the local `lib/NetDevPack` project, adding `shared/OpenLineOps.Infrastructure.Data.Core`, adding the `OpenLineOps.SampleInspection` EF/Data.Core bounded-context living template, adding the Devices `EfSqlite` provider, and updating the bounded-context scaffolder tool.
- `OpenLineOps.Infrastructure.Data.Core` build: passed with zero warnings.
- `tests/OpenLineOps.Infrastructure.Data.Core.Tests` build: passed with zero warnings.
- `tests/OpenLineOps.SampleInspection.Tests` tests: passed, 3 total, covering SQLite relational persistence through EF migrations, Data.Core Unit of Work, OpenLineOps domain event dispatch, shared strong typed ID conversion, and pending-migration verification.
- `tools/OpenLineOps.BoundedContext.Scaffolder` build: passed with zero warnings.
- `tests/OpenLineOps.BoundedContext.Scaffolder.Tests` tests: passed, 9 total, covering modular DDD scaffold generation, Data.Core/EF template content, integration-event template wiring, overwrite protection, argument parsing, solution update command construction, and identifier validation.
- Bounded-context scaffolder command smoke: `--help` passed, generated `Quality/InspectionPlan` under `output/scaffolder-migration-smoke-*`, generated Infra.Data project with deterministic initial migration built with zero warnings, and generated SQLite persistence/application-service tests passed with 3 total tests.
- `OpenLineOps.sln` build: passed after the Operations PostgreSQL provider, CAP EF transaction PostgreSQL integration coverage, Operations PostgreSQL deployment documentation, EventBus configuration fail-fast additions, PostgreSQL readiness health-check additions, RabbitMQ readiness registration additions, and release manifest tooling addition, with zero warnings.
- `OpenLineOps.sln` tests: passed, 340 total with `dotnet test OpenLineOps.sln --no-build`.
- `OpenLineOps.slnx` build: passed with zero warnings after the release manifest tool and tests were added.
- `OpenLineOps.slnx` serial tests: passed, 340 total with `dotnet test OpenLineOps.slnx --no-build -m:1 -v minimal`.
- `tests/OpenLineOps.Domain.Abstractions.Tests` tests: passed, 9 total, including NetDevPack aggregate compatibility, Unit of Work repository contracts, integration event metadata discovery, converter registry conversion, and NetDevPack value object equality.
- `tests/OpenLineOps.Infrastructure.Data.Core.Tests` tests: passed, 7 total, including BaseDbContext OpenLineOps domain event dispatch, OpenLineOps integration publishing, optional transaction-coordinator publishing, NetDevPack domain/integration event flow, strong typed ID EF conversion, repository query, and repository removal.
- `tests/OpenLineOps.Runtime.Tests` tests: passed, 47 total, including PythonScript runtime execution, real process-isolated worker execution, Runtime automation plan dispatch, explicit `command.execute` automation action dispatch, and Python script worker sandbox start-info/policy coverage.
- `tests/OpenLineOps.Processes.Tests` tests: passed, 36 total, including real PythonScript syntax validation, versioned Process Blockly block catalog coverage, generated catalog source coverage, and SQLite Blockly block catalog persistence coverage.
- `tests/OpenLineOps.Devices.Tests` tests: passed, 43 total, including EF/Data.Core SQLite device aggregate round trip, station query, update path, Unit of Work commit, domain event dispatch/clearing, SQLite snapshot import into EF SQLite, and migration bootstrap for EF `EnsureCreated` databases.
- `tests/OpenLineOps.Api.Tests` tests: passed, 74 total, including PythonScript process runtime launch, ProcessIsolated DI selection, Python script worker sandbox configuration binding, Devices configuration API coverage, Devices default `EfSqlite` persistence, explicit InMemory persistence selection, Operations alarm API coverage, Operations PostgreSQL provider-profile DI coverage, Runtime-to-Operations incident alarm bridge coverage, PostgreSQL and RabbitMQ readiness health-check registration coverage, Plugins management API coverage, versioned Process Blockly block API coverage, and sample plugin manifest-generated Blockly block discovery.
- `tests/OpenLineOps.Operations.Tests` tests: passed, 10 total, including aggregate lifecycle behavior, SQLite/Data.Core persistence through EF migrations, OpenLineOps integration event publishing, alarm integration DTO conversion, open-station alarm queries, app-service lifecycle use cases, first-write migration bootstrap, and pending-migration verification.
- `tests/OpenLineOps.PostgresIntegration.Tests` build: passed; default test run skipped 6 container-backed PostgreSQL tests, including the Operations PostgreSQL app-service persistence profile and the CAP EF transaction coordinator PostgreSQL outbox profile, until `OPENLINEOPS_RUN_POSTGRES_INTEGRATION=1` is set.
- `tests/OpenLineOps.EventBus.Tests` tests: passed, 7 total, including CAP publisher domain-event-to-integration-DTO conversion, integration event topic/header publishing behavior, compatibility publish failure handling, strict transaction publish failure behavior, EventBus PostgreSQL connection-string fail-fast validation, invalid in-memory transaction-coordinator validation, and PostgreSQL CAP storage transaction-coordinator registration.
- `tests/OpenLineOps.ReleaseManifest.Tests` tests: passed, 3 total, including release manifest/checksum/release-notes generation, stale output-file exclusion, empty artifact directory failure, and command validation.
- Desktop `npm run typecheck`: passed.
- Desktop `npm run build`: passed, with Blockly split into a lazy `blockly` chunk and the Processes workbench split from the dashboard bundle.
- Desktop `npm run smoke:e2e`: passed, including the official Blockly workspace, user-defined Blockly block registration, version history restore from v1 to v3, Trace workbench search/detail/export, Devices workbench registration/connection, and Plugins workbench lifecycle start/stop through the rendered UI.
- Desktop `npm audit --audit-level=high --registry=https://registry.npmjs.org`: passed, zero vulnerabilities reported.
- Release manifest command smoke: `--help` passed.
- `dotnet list OpenLineOps.sln package --vulnerable --include-transitive`: passed after the EF SQLite template, Devices `EfSqlite`, modular DDD scaffolder update, CAP EventBus package addition, EF/CAP transaction-coordinator dependency update, Devices EF migration addition, Operations/SampleInspection/scaffolder migration additions, Operations Npgsql EF provider addition, EventBus configuration fail-fast addition, API PostgreSQL readiness health-check Npgsql reference, API RabbitMQ readiness health-check RabbitMQ.Client reference, and release manifest tool addition, no vulnerable packages reported from current configured sources.
- `lib/pythonscript/PythonScript.sln` build: passed for `net8.0` and `net10.0` with zero warnings.
- Full-solution format verification without the excludes is intentionally not used because `lib/pythonscript` and `lib/NetDevPack` are local component directories with independent whitespace, line-ending, and encoding conventions.

Current result on 2026-07-09:

- `OpenLineOps.sln` restore: passed.
- `OpenLineOps.slnx` restore: passed.
- Whitespace format verification excluding `lib/pythonscript` and `lib/NetDevPack`: passed.
- Style warning format verification excluding `lib/pythonscript` and `lib/NetDevPack`: passed.
- `OpenLineOps.sln` serial build: passed with zero warnings.
- `OpenLineOps.slnx` serial build: passed with zero warnings.
- `OpenLineOps.sln` tests: passed, 355 total.
- `OpenLineOps.slnx` serial tests: passed, 355 total.
- `tests/OpenLineOps.PostgresIntegration.Tests` default test run: passed with 7 skipped container-backed tests, including the new RabbitMQ EventBus readiness integration test, because no opt-in container environment variable was set.
- Optional PostgreSQL and RabbitMQ container execution: not run in the current workspace because the `docker` CLI is not installed.
- `dotnet list OpenLineOps.sln package --vulnerable --include-transitive`: passed, no vulnerable packages reported from current configured sources.
- `dotnet list tests/OpenLineOps.PostgresIntegration.Tests/OpenLineOps.PostgresIntegration.Tests.csproj package --vulnerable --include-transitive`: passed, no vulnerable packages reported from current configured sources.
- Release manifest command smoke: `--help` passed with the repeatable `--require-kind <kind>` option and `--verify` mode documented in command usage.
- `tests/OpenLineOps.ReleaseManifest.Tests` tests: passed, 8 total, including artifact kind inference, repeatable `--require-kind` validation, generated release verification, tampered artifact hash detection, and CLI verify-mode execution.
- Open-source metadata verification script: passed, including root MIT license text, README license wording, default .NET package metadata, and Electron desktop package MIT license metadata.
- Third-party license metadata verification script: passed, covering 117 NuGet packages, 259 NPM packages, and 16 unique license values from `OpenLineOps.sln` restore and lockfile metadata.
- Third-party notice generation with `-UpdateNotice`: passed and generated root `THIRD-PARTY-NOTICES.md`; default verification then passed with the notice synchronized to solution restore and lockfile metadata.
- Release dependency inventory generation and verification: passed from the third-party license metadata verifier with 117 NuGet packages, 259 NPM packages, and 16 unique license values; generated `release-dependency-inventory.json` is verified by release candidate inspection and hashed in release provenance.
- Release metadata checksum generation and verification: passed with `release-metadata-checksums.sha256` covering manifest, artifact checksums, release notes, dependency inventory, and provenance metadata.
- Release artifact kind gate script: passed with source plus 5 binary/development artifact kinds and generated schema version 2 manifest under `artifacts/release-gate`.
- Release staging script: passed with source archive, Release `dotnet publish` outputs for API, plugin host, script worker, sample plugin, desktop production build output, optional desktop signing hook left disabled, 6 zip artifacts, schema version 2 manifest, checksums, release notes, dependency inventory, provenance metadata, and metadata checksums under `artifacts/release`.
- CI-equivalent release staging script with `-SkipDesktopBuild`: passed with Release publish outputs, the existing desktop production build output, 6 zip artifacts, schema version 2 manifest, checksums, release notes, dependency inventory, provenance metadata, metadata checksums, and source archive coverage for `THIRD-PARTY-NOTICES.md` under `artifacts/release`.
- Release manifest verify command: passed against the current `artifacts/release` directory, including all 6 required artifact kinds and checksum-file parity.
- Release candidate inspection script: passed against `artifacts/release`, including provenance metadata verification, dependency inventory metadata verification, metadata checksum verification, release-note coverage, safe zip entry path checks, source archive sensitive-file checks, expected zip entries for all 6 artifact kinds, source archive `THIRD-PARTY-NOTICES.md` coverage, and desktop archive entry-point checks.
- Release candidate inspection verification script: passed with a positive fixture plus unsafe-path, sensitive-source, bad-provenance, missing-provenance, missing-dependency-inventory, bad-dependency-inventory, missing-metadata-checksums, and bad-metadata-checksums negative fixtures.
- Publication evidence script: passed and generated `output/publication-evidence/publication-evidence.json` plus `.md`, while recording pending external items for final MIT confirmation and the unsigned desktop release archive when external proof arguments are not supplied.
- Publication evidence verification script: passed with default evidence, confirmed MIT/GitHub proof, invalid GitHub Actions URL, and `-RequirePublishable` cases.
- Publication evidence child work-directory isolation: passed by rerunning evidence generation and evidence verification after isolating child gate temporary folders.
- Final publication preflight verification script: passed with missing license confirmation, invalid GitHub Actions URL, missing signing selector, and valid `-PlanOnly` command-chain cases.
- Final publication preflight evidence output: passed with generated `publication-preflight.json` and `.md` diagnostics under `output/final-publication-preflight`.
- Release candidate inspection with `-RequireSignedDesktop`: failed as expected on the current unsigned desktop archive with `OpenLineOps.exe` signature status `NotSigned`.
- CI workflow action reference verification script: passed, enforcing approved version-pinned action refs for checkout, .NET setup, Node setup, and `actions/upload-artifact@v7`, plus the release artifact and diagnostics upload contract.
- CI workflow Python.NET runner setup: added `actions/setup-python@v6` with Python 3.12 and a Windows runner `PYTHONNET_PYDLL` discovery step that prefers the version-specific Python runtime DLL, such as `python312.dll`, so Python script validation and runtime tests can execute on GitHub-hosted Windows agents.
- CI release artifact bundle inspection script: passed against the local CI-equivalent bundle shape, including release candidate inspection, dependency inventory metadata, metadata checksums, publication evidence, evidence verification diagnostics, final publication preflight diagnostics, required artifact kind parity, and generated `ci-release-artifact-inspection.json` plus `.md` diagnostics.
- CI release artifact bundle inspection with `-RequirePublishable`: failed as expected on the current unsigned/non-final bundle while still writing a failed inspection report under `output/ci-release-artifact-inspection-require-publishable`.
- GitHub Actions public repository workflow proof: passed on run `29011973592` for commit `2d08e4e83393033ca8862309f1101aef460602a1`, including restore, format, build, .NET tests, PythonScript component build, release staging, release candidate inspection, publication evidence verification, final publication preflight verification, CI release artifact bundle inspection, Electron desktop smoke, desktop audit, and artifact upload.
- CI workflow release artifact upload execution: passed on GitHub Actions run `29011973592` with `actions/upload-artifact@v7`, missing-file failure, 14-day retention, compression disabled for already-zipped release payloads, and uploaded `artifacts/release` plus diagnostics.
- CI workflow publication readiness gate execution: passed on GitHub Actions run `29011973592` with `eng/verify-publication-readiness.ps1 -AllowPendingExternal`, preserving the unsigned desktop archive as the remaining external publication warning.
- Publication readiness gate with `-AllowPendingExternal`: passed while warning about the remaining external publication blocker: the unsigned desktop release archive.
- Strict publication readiness gate: failed as intended on the unsigned desktop release archive, so the gate will block public release until a signed desktop release archive is finalized.
- Desktop `node --check scripts/package-win-unpacked.mjs`: passed.
- Desktop `npm run package:win:ci`: passed and generated `apps/desktop/release/desktop/win-unpacked/OpenLineOps.exe`.
- Desktop release archive inspection: passed; `desktop-openlineops-0.0.0-local.zip` contains `package/win-unpacked/OpenLineOps.exe`, package notes, 87 unpacked package entries, and no unsafe zip entry paths.
- Source archive inspection: passed; generated desktop `release` outputs are excluded while `THIRD-PARTY-NOTICES.md`, `eng/verify-ci-workflow-actions.ps1`, `eng/inspect-ci-release-artifact.ps1`, `eng/verify-third-party-license-metadata.ps1`, and `apps/desktop/scripts/package-win-unpacked.mjs` are included, with no unsafe zip entry paths and no sensitive source archive entries.
- Desktop `npm audit --audit-level=high --registry=https://registry.npmjs.org`: passed, zero vulnerabilities reported.
- Desktop signing readiness verification script: passed with missing-selector, multiple-selector, and deterministic signing-plan checks under `output/desktop-signing-readiness`.
- Publication metadata finalization invalid URL path: failed as expected when `RepositoryUrl` was not exactly `https://github.com/<owner>/<repository>`.
- Publication metadata finalization smoke: passed against `output/finalize-publication-smoke`, producing final `SECURITY.md`, `CODE_OF_CONDUCT.md`, and issue-template security policy URL content with no pre-publication wording left in the generated files.
- Publication metadata finalization verification script: passed with invalid URL, placeholder contact, and generated metadata content checks under `output/publication-metadata-finalization`.
- `tests/OpenLineOps.Processes.Tests` build: passed with zero warnings after adding explicit loop-policy model and validation coverage.
- `tests/OpenLineOps.Processes.Tests` tests: passed, 40 total.
- `tests/OpenLineOps.Runtime.Tests` build: passed with zero warnings after adding graph/Decision runtime execution and counted loop traversal coverage.
- `tests/OpenLineOps.Runtime.Tests` tests: passed, 51 total.
- `tests/OpenLineOps.Api.Tests` build: passed with zero warnings after adding published-process Decision branch and counted loop-policy runtime launch coverage.
- `tests/OpenLineOps.Api.Tests` tests: passed, 76 total.
- Desktop `npm run typecheck`: passed after adding transition loop-policy contracts and editor fields.
- Desktop `npm run build`: passed after adding transition loop-policy editor support.
- Desktop `node --check scripts/electron-smoke.mjs`: passed after hardening generated transition-id discovery.
- Desktop `npm run smoke:e2e`: passed after adding Decision-controlled counted loop-policy UI editing, API persistence verification, process publication, and published-runtime launch coverage.

## Current-model note (2026-07-10)

The dated delivery logs above are historical evidence, not current contracts. References in those logs to `automation_plan`, `RuntimeAutomationPlanDispatcher`, global `/api/process-definitions` or `/api/process-blocks` routes, simulated runtime starts, global process persistence, and legacy runtime configuration resolvers describe deleted implementations. The current model compiles Blockly directly to Flow IR v2, keeps Python as one controlled published action, scopes authoring to Project/Application resources, and starts runtime only from immutable project release snapshots. No compatibility path exists for those removed surfaces or for runtime snapshots missing ActionId, TargetKind, or TargetId.
