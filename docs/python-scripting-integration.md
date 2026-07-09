# Python Scripting Integration

OpenLineOps must use the in-repository Python scripting component for Python-based flow logic:

- Component path: `lib/pythonscript`
- Current component project: `lib/pythonscript/PythonScript/PythonScript.csproj`
- Current runtime stack: `pythonnet`
- Current target frameworks: `net8.0;net10.0`

This document records the current integration direction and the runtime contract used by the backend.

## Product Rule

When OpenLineOps needs Python script execution or Python script validation, it should use the in-repository `PythonScript` component instead of introducing a second Python runtime wrapper.

When OpenLineOps needs script editing in the desktop flow editor:

- Blockly is the default authoring mode.
- Manual Python code editing remains available for advanced users.
- The saved script artifact should preserve both the Blockly workspace model and generated or manually edited Python source.

## Architecture Boundary

The Process domain should not reference `pythonnet`, `PythonScript`, Electron, or Blockly packages.

Recommended layering:

- Process domain: stores script node identity, language, version, mode, content references, declared inputs, declared outputs, and validation status.
- Process application: validates publish rules through `IProcessScriptDefinitionValidator`.
- Runtime application: invokes script execution through `IRuntimeScriptExecutor`.
- Infrastructure: implements Python validation and execution with `lib/pythonscript`.
- Electron: owns Blockly workspace editing, Python source preview, manual editing, and validation UI.

## Script Node Model

The current Process domain node model carries:

- `nodeId`
- `displayName`
- `scriptLanguage`: currently `Python`
- `scriptEditorMode`: `Blockly` or `ManualCode`
- `blocklyWorkspaceJson`
- `scriptSourceCode`
- `scriptSourceHash`
- `scriptVersion`
- `scriptTimeout`
- `inputPayload`: optional runtime input passed to the script execution scope

Future script-node revisions should add declared input bindings, declared output bindings, validation status, and explicit cancellation policy.

Published process versions should freeze the script source and Blockly workspace snapshot.

## Current Implementation Status

Implemented:

- `ProcessNodeKind.PythonScript`.
- `ProcessScriptEditorMode` with `Blockly` and `ManualCode`.
- Application and API create/query DTOs for Python script metadata.
- API creation defaults an omitted Python script editor mode to `Blockly`.
- Graph validation blocks publish when Python script nodes lack editor mode, Blockly workspace JSON in Blockly mode, source code, source hash, script version, or positive timeout.
- `IProcessScriptDefinitionValidator` application port.
- `PythonScriptDefinitionValidator` infrastructure adapter that calls `PythonScript.SyntaxStaticCheck.PythonSyntaxChecker`.
- Publish-time Python syntax validation before the process definition aggregate is marked published.
- Python DLL auto-discovery for pythonnet through the local `python` CLI when `PYTHONNET_PYDLL` is not already configured.
- SQLite/PostgreSQL aggregate snapshot mapping preserves Python script metadata.
- SQLite/PostgreSQL aggregate snapshot mapping preserves Python script input payload.
- `IRuntimeScriptExecutor` application port.
- `RuntimeScriptCommandPayload` command payload contract for script execution.
- `ConfigurableRuntimeScriptExecutor` infrastructure adapter that selects the configured Python execution mode.
- `PythonScriptRuntimeScriptExecutor` trusted in-process adapter that executes published scripts through `PythonScript.Runtime.PythonRuntimeSession`.
- `OpenLineOps.ScriptWorker` process worker that executes one Python script request per process through the same `PythonScript` execution scope.
- `ProcessIsolatedPythonScriptRuntimeScriptExecutor` adapter that starts the configured worker process, sends a JSON request over stdin, and reads the terminal result from stdout.
- Runtime command dispatchers in Runtime and Devices modules route Python script commands to the script executor before simulator, device-backed, or plugin execution.
- Process runtime launcher maps published Python script nodes to the special runtime command `process.python-script` / `PythonScript.Execute`.
- `lib/pythonscript/PythonScript/PythonScript.csproj` now multi-targets `net8.0;net10.0`.
- Process-isolated Python worker execution supports sandbox launch modes for direct external process, container runtime, and least-privilege identity launcher.
- Electron Processes workbench provides an official Blockly workspace for PythonScript automation blocks, generated Python source preview, and manual Python code mode over the same backend process contracts.
- Current Blockly blocks cover initial automation semantics: axis movement, light output, motor rotation, wait, and result output.
- Process Blockly block catalog API exposes built-in blocks and accepts user-registered custom blocks.
- User-registered Blockly block definitions are persisted through the same process persistence provider selection as process definitions: in-memory for transient development, SQLite for local desktop mode, and PostgreSQL for deployment mode.
- User-registered Blockly block definitions are versioned. Re-registering the same custom `blockType` creates a new version, list endpoints return the latest version, and `/api/process-blocks/{blockType}/versions` exposes the version history.
- The desktop Block Catalog panel can browse custom Blockly block versions and restore an older version by re-registering it as the latest version.
- Custom Blockly blocks store Blockly JSON and a Python code template. The desktop renderer applies placeholders such as `{{FIELD}}`, `{{number:FIELD}}`, and `{{raw:FIELD}}` to generate Python source without executing user-provided frontend JavaScript.
- Process Blockly block catalogs can merge read-only generated block sources. The API module currently generates Blockly blocks from compatible plugin device and process command manifests.
- Manifest-generated blocks emit `command.execute` automation actions with explicit capability, command, payload, command definition id, plugin metadata, and timeout.
- Blockly-generated Python emits a structured `automation_plan` result.
- Runtime automation dispatch maps `automation_plan` actions to child runtime commands so Blockly-authored flows can execute through simulator, device-backed, or plugin-backed command routes.
- Runtime automation dispatch appends an `automation_dispatch` array to the script result payload and stops on the first failed, rejected, timed out, or canceled action.

Not implemented yet:

- Hard interruption of already-running Python code inside the in-process pythonnet adapter.

## Validation Flow

The desktop editor should validate in this order:

1. Blockly workspace structure, when `editorMode` is `Blockly`.
2. Generated Python source.
3. Python syntax through `PythonScript.SyntaxStaticCheck.PythonSyntaxChecker`.
4. OpenLineOps script contract checks, including declared inputs, outputs, timeout, and forbidden imports if configured.
5. Process graph validation.

Manual code mode skips Blockly structure validation but still runs Python syntax and OpenLineOps contract checks.

If pythonnet cannot locate the Python runtime DLL automatically, set `PYTHONNET_PYDLL` to the full Python DLL path before starting the API. On Windows this is typically a path such as `C:\Python313\python313.dll`.

## Runtime Flow

Runtime execution should not execute Python directly from the domain model.

Current runtime path:

1. Processes loads the published process definition and validates it again before runtime start.
2. A `PythonScript` process node is mapped to an `ExecutableRuntimeNode` with target capability `process.python-script` and command name `PythonScript.Execute`.
3. The runtime node input payload is JSON serialized as `RuntimeScriptCommandPayload`:
   - `scriptLanguage`
   - `scriptSourceCode`
   - `scriptVersion`
   - `inputPayload`
4. Runtime creates a normal `RuntimeCommand` and `RuntimeCommandExecutionContext`; existing command trace, monitoring, persistence, and SignalR event flow remain unchanged.
5. Configurable runtime command executors route the special script command to `IRuntimeScriptExecutor`.
6. `ConfigurableRuntimeScriptExecutor` selects either trusted in-process execution or process-isolated worker execution.
7. If the script defines a variable named `result`, the adapter serializes it to compact JSON and stores it as the runtime command result payload. If `result` is not defined, the command completes with a null result payload.
8. If the completed script result contains an `automation_plan` array, `RuntimeAutomationPlanDispatcher` maps each action into the existing runtime command pipeline.
9. Supported initial actions are `axis.move` -> `motion.axis` / `MoveAxis`, `io.light` -> `io.light` / `SetLight`, `motor.rotate` -> `motion.motor` / `RotateMotor`, `command.execute` with explicit `capability` and `command`, and `flow.wait` as a local runtime delay.
10. Python exceptions become failed runtime command results.

The current Python scope exposes these variables:

- `input_payload`
- `script_version`
- `session_id`
- `station_id`
- `configuration_snapshot_id`
- `node_id`
- `command_id`

Example script:

```python
automation_plan = []
result = {'automation_plan': automation_plan}
automation_plan.append({
    'type': 'axis.move',
    'axis': 'X',
    'position': 10,
    'unit': 'mm',
    'speed': 5,
})
result['normalized'] = input_payload
result['status'] = 'ok'
result['node'] = node_id
```

## Execution Modes

Python runtime execution is configured through `OpenLineOps:Runtime:Scripting:Python`.

Default development mode:

```json
{
  "OpenLineOps": {
    "Runtime": {
      "Scripting": {
        "Python": {
          "ExecutionMode": "InProcessTrusted"
        }
      }
    }
  }
}
```

Process-isolated mode:

```json
{
  "OpenLineOps": {
    "Runtime": {
      "Scripting": {
        "Python": {
          "ExecutionMode": "ProcessIsolated",
          "WorkerFileName": "dotnet",
          "WorkerArguments": "path/to/OpenLineOps.ScriptWorker.dll"
        }
      }
    }
  }
}
```

Container-isolated worker launch:

```json
{
  "OpenLineOps": {
    "Runtime": {
      "Scripting": {
        "Python": {
          "ExecutionMode": "ProcessIsolated",
          "WorkerFileName": "dotnet",
          "WorkerArguments": "path/to/OpenLineOps.ScriptWorker.dll",
          "WorkerWorkingDirectory": "path/to/repository-or-worker-folder",
          "Sandbox": {
            "RequireLeastPrivilegeExecution": true,
            "IsolationMode": "Container",
            "ContainerRuntimeExecutable": "podman",
            "ContainerImage": "openlineops/script-worker:1.0.0",
            "ContainerMountSource": "path/to/repository-or-worker-folder",
            "ContainerWorkspacePath": "/openlineops/script-worker",
            "LeastPrivilegeIdentity": "10001:10001",
            "AdditionalContainerRunArguments": ["--pull=never"]
          }
        }
      }
    }
  }
}
```

Least-privilege account worker launch:

```json
{
  "OpenLineOps": {
    "Runtime": {
      "Scripting": {
        "Python": {
          "ExecutionMode": "ProcessIsolated",
          "WorkerFileName": "dotnet",
          "WorkerArguments": "path/to/OpenLineOps.ScriptWorker.dll",
          "Sandbox": {
            "RequireLeastPrivilegeExecution": true,
            "IsolationMode": "LeastPrivilegeIdentity",
            "LeastPrivilegeIdentity": "openlineops-script",
            "LeastPrivilegeLauncherExecutable": "sudo"
          }
        }
      }
    }
  }
}
```

Accepted aliases:

- `InProcessTrusted`, `InProcess`, `Trusted`
- `ProcessIsolated`, `Worker`, `ExternalProcess`
- Sandbox isolation: `ExternalProcess`, `LeastPrivilegeIdentity`, `Sudo`, `RunAs`, `Container`, `Docker`, `Podman`

The process-isolated adapter starts a fresh worker process per script command. The worker reads a single JSON execution request from stdin, suppresses Python `stdout` and `stderr` during script execution so prints do not corrupt the protocol, writes one JSON terminal result to stdout, and exits. Runtime command timeout or cancellation kills the worker process tree.

Container mode starts the configured container runtime with `run --rm --interactive`, mounts the configured worker workspace, maps host worker paths into the container workspace path, and can apply `--network none`, `--security-opt no-new-privileges`, `--cap-drop ALL`, `--read-only`, `--pids-limit`, `--user`, and additional configured run arguments. Least-privilege identity mode launches through `sudo` by default on Unix-style hosts, or through a deployment-provided launcher executable and argument template on platforms where `sudo` is not appropriate.

## Compatibility Work

The selected compatibility path is multi-targeting `PythonScript` to `net8.0;net10.0` so .NET 8 consumers can use the component while OpenLineOps references the `net10.0` target.

Do not add a duplicate Python wrapper to OpenLineOps. Production isolation is provided by placing the in-repository `PythonScript` component behind `OpenLineOps.ScriptWorker`.

## Safety Rules

- Treat Python execution as untrusted unless the script source is first-party and signed.
- Treat `InProcessTrusted` as a development or first-party trusted-script mode only.
- Prefer `ProcessIsolated` for production execution.
- For stronger production sandboxing, configure `OpenLineOps.ScriptWorker` with container or least-privilege identity isolation. This still depends on the host/container platform enforcing the boundary correctly.
- Runtime command timeout and cancellation can kill the worker process tree in `ProcessIsolated` mode. The in-process pythonnet adapter still cannot safely terminate arbitrary running Python code inside the host process.
- Keep Python GIL/threading constraints inside infrastructure.
- Store script execution logs and exceptions as traceable artifacts or runtime events.
- Never leak `pythonnet` types into domain, application, API, or Electron contracts.

## Electron Editor Direction

Initial desktop flow editor should provide:

- Official Blockly canvas as the default editor.
- Automation blocks for motion, I/O, timed waits, and result shaping.
- Python code preview generated from Blockly.
- Manual code mode toggle.
- Syntax validation action.
- Publish blocking on validation errors.
- Error list with line, column, message, and source.
- Stable serialization of Blockly JSON and Python source.

The UI should not describe architecture rules to operators; those rules belong in docs and developer tooling.
