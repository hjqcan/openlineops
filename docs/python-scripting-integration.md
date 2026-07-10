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

Trusted in-process development mode:

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

Process-isolated production mode:

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
            "IsolationMode": "Container",
            "ContainerRuntimeExecutable": "podman",
            "ContainerImage": "openlineops/script-worker:1.0.0",
            "NetworkDisabled": true
          }
        }
      }
    }
  }
}
```

The isolated adapter starts a worker process for the script command, exchanges one JSON request/result over standard streams, and kills the process tree on cancellation or timeout. Container and least-privilege launch policies remain infrastructure concerns.

## Safety rules

- Treat Python as dynamic code and make its presence explicit in the process graph and release.
- Use `InProcessTrusted` only for trusted first-party development scenarios.
- Prefer process or container isolation for production.
- Never expose `pythonnet` types outside Infrastructure.
- Record script results, exceptions, timeout and cancellation through the standard runtime command and trace lifecycle.
- Do not recreate Blockly-to-Python generation, dual-source persistence, editor-mode switches, or automation-plan dispatch compatibility paths.
