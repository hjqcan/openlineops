# Python Scripting Integration

OpenLineOps uses the in-repository `lib/pythonscript` component for explicit advanced Python flow logic. Blockly does not use the Python runtime.

## Product rule

- Blockly is the primary automation authoring surface.
- `Blockly` and `PythonScript` are separate `ProcessNodeKind` values.
- A Blockly node persists only its workspace and compiles server-side to static Flow IR actions.
- A PythonScript node persists only Python source, source hash, version, input and timeout.
- The desktop never generates or stores Python for a Blockly workspace.
- Custom or plugin-generated Blockly blocks cannot contain Python templates.

This separation is intentional. Visual automation remains statically analyzable and target-bound; Python remains an explicit advanced escape hatch whose dynamic behavior is visible in the graph and release.

## Architecture boundary

- Processes Domain owns the distinct Blockly and PythonScript node invariants without referencing Blockly, Electron, `pythonnet`, or the Python component.
- Processes Application parses declarative Blockly workspaces and Runtime Action Contracts into Flow IR.
- Runtime Application invokes Python through `IRuntimeScriptExecutor` only for PythonScript actions.
- Infrastructure implements validation and execution using `lib/pythonscript`.
- Electron edits Blockly workspace JSON or Python source according to the selected node kind.

## Blockly path

Publication performs this path:

1. Parse the exact current Blockly workspace serialization.
2. Resolve every block type to one exact catalog version.
3. Validate each block's fields against its canonical Runtime Action Contract.
4. Resolve explicit `TARGET_KIND` and `TARGET_ID` references against the selected Application topology.
5. Emit static Flow IR actions with source block id, block type, action type, target, command, input, timeout and retry policy.
6. Freeze block version, canonical contract hash and provider package dependency locks in the immutable release.

Unknown blocks, obsolete workspace shapes, legacy Python-template blocks, missing targets and hash mismatches fail publication. Runtime never reads the live block catalog or plugin inventory.

## PythonScript node model

An explicit PythonScript node carries:

- `nodeId` and `displayName`;
- `scriptLanguage`: `Python`;
- `scriptSourceCode` and `scriptSourceHash`;
- `scriptVersion`;
- positive script timeout;
- optional runtime `inputPayload`.

It must not contain Blockly workspace JSON. A Blockly node must not contain Python source, source hash, language or script version.

Publish-time syntax validation uses `PythonScript.SyntaxStaticCheck.PythonSyntaxChecker`. The runtime maps a published PythonScript node to the governed `process.python-script` / `PythonScript.Execute` command and executes it through `IRuntimeScriptExecutor`.

## Runtime execution modes

Python execution is configured under `OpenLineOps:Runtime:Scripting:Python`.
The only accepted execution mode is process-isolated:

```json
{
  "OpenLineOps": {
    "Runtime": {
      "Scripting": {
        "Python": {
          "ExecutionMode": "ProcessIsolated",
          "WorkerFileName": "C:/Program Files/OpenLineOps/StationAgent/OpenLineOps.ScriptWorker.exe",
          "Sandbox": {
            "RequireLeastPrivilegeExecution": true,
            "IsolationMode": "Container",
            "ContainerRuntimeExecutable": "podman",
            "ContainerImage": "openlineops/script-worker@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "ContainerNetwork": "none"
          }
        }
      }
    }
  }
}
```

The adapter starts a worker process for every script command, exchanges one JSON
request/result over standard streams, and kills the process tree on cancellation
or timeout. There is no in-process fallback. Container and least-privilege launch
policies remain infrastructure concerns.

These generic Runtime container settings are only for a different host that
ships and verifies a real container-native worker image. Station Agent does not
bind or forward the container fields. On a production Station,
`StationAgentHostOptions` validates the co-packaged Windows worker, the absolute
host Python runtime DLL, the signed co-packaged
`OpenLineOps.LeastPrivilegeLauncher.exe`, and the fixed
`PerExecutionAppContainer` identity. The launcher creates a unique
AppContainer profile and package SID for each command; Windows supplies Low
integrity (RID 4096), no network capability is granted, and a kill-on-close Job
Object owns the complete process tree. It cannot be replaced or given a custom
argument template.
The host Python tree is authorized only by the launcher's explicit
`provision-python-runtime --runtime-dll <absolute-path>` administrator command
after installation or upgrade. Script execution verifies that provisioning and
never mutates the Python DACL at run time.
`ProcessStationRuntimeHost` supplies only those canonical values after clearing
the inherited environment. `ExternalProcess` remains available only when
required least privilege is explicitly disabled for development or testing.
The Agent bundle manifest records Agent, Station Runtime, Plugin Host, and
Python Script Worker as distinct self-contained entry points.

Execution and isolation tokens are case-sensitive. `ExecutionMode` accepts only
`ProcessIsolated`; sandbox `IsolationMode` accepts only
`ExternalProcess`, `LeastPrivilegeIdentity`, or `Container`. Select Docker, Podman, or
another compatible container engine with `ContainerRuntimeExecutable`. Unknown, empty,
or differently cased mode tokens fail during module registration.

Each script receives immutable, command-scoped values: `input_payload`,
`script_version`, `session_id`, `production_run_id`,
`production_line_definition_id`, `production_stage_id`, `stage_sequence`,
`workstation_id`, `dut_model_id`, `dut_identity_input_key`,
`dut_identity_value`, `station_id`, `configuration_snapshot_id`, `project_id`,
`application_id`, `project_snapshot_id`, `node_id`, `command_id`, `action_id`,
`target_capability`, `target_kind`, `target_id`, and `command_name`. Assign the
script output to `result`; the worker serializes it as one JSON value for the
standard Runtime command and Trace lifecycle.

## Safety rules

- Treat Python as dynamic code and make its presence explicit in the process graph and release.
- Execute every script through the dedicated worker process; use container or
  least-privilege identity isolation where the deployment policy requires it.
- Never expose `pythonnet` types outside Infrastructure.
- Record script results, exceptions, timeout and cancellation through the standard runtime command and trace lifecycle.
- Do not recreate Blockly-to-Python generation, dual-source persistence, editor-mode switches, or automation-plan dispatch compatibility paths.
