using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Scripting;
using OpenLineOps.ScriptWorker;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProcessIsolatedPythonScriptRuntimeScriptExecutorTests
{
    [Fact]
    public async Task ProcessIsolatedExecutorRunsPythonSourceThroughWorkerProcess()
    {
        var workerAssemblyPath = typeof(PythonScriptWorkerProgram).Assembly.Location;
        var executor = new ProcessIsolatedPythonScriptRuntimeScriptExecutor(new PythonScriptRuntimeOptions
        {
            WorkerFileName = "dotnet",
            WorkerArguments = $"\"{workerAssemblyPath}\""
        });
        var request = CreateRequest(
            """
            print('captured by worker')
            result = {
                'normalized': input_payload,
                'session': session_id,
                'run': production_run_id,
                'line': production_line_definition_id,
                'operation': operation_id,
                'attempt': operation_attempt,
                'station_system': station_system_id,
                'production_unit': {
                    'model': product_model_id,
                    'input_key': production_unit_identity_input_key,
                    'value': production_unit_identity_value
                },
                'release': [project_id, application_id, project_snapshot_id],
                'action': action_id,
                'target': [target_kind, target_id, target_capability],
                'command': command_name
            }
            """,
            inputPayload: "scan-ok");

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Payload);
        Assert.Contains("\"normalized\":\"scan-ok\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"session\":\"00000000-0000-0000-0000-000000000001\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"run\":\"10000000-0000-0000-0000-000000000001\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"line\":\"line.main\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"operation\":\"operation.main\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"attempt\":1", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"station_system\":\"station.main\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"product.main\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"input_key\":\"serialNumber\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"value\":\"UNIT-001\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"release\":[\"project.main\",\"application.main\",\"snapshot.release\"]", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"node-normalize:action:1\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"target\":[\"Capability\",\"process.python-script\",\"process.python-script\"]", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"command\":\"PythonScript.Execute\"", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessIsolatedExecutorRejectsMissingWorkerCommand()
    {
        var executor = new ProcessIsolatedPythonScriptRuntimeScriptExecutor(new PythonScriptRuntimeOptions
        {
            ExecutionMode = PythonScriptRuntimeExecutionModes.ProcessIsolated
        });

        var result = await executor.ExecuteAsync(CreateRequest("result = 1"));

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("WorkerFileName", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessIsolatedExecutorRejectsRequiredLeastPrivilegeWithoutIsolation()
    {
        var executor = new ProcessIsolatedPythonScriptRuntimeScriptExecutor(new PythonScriptRuntimeOptions
        {
            WorkerFileName = "dotnet",
            WorkerArguments = "\"OpenLineOps.ScriptWorker.dll\"",
            Sandbox = new PythonScriptWorkerSandboxOptions
            {
                RequireLeastPrivilegeExecution = true
            }
        });

        var result = await executor.ExecuteAsync(CreateRequest("result = 1"));

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("least-privilege execution", result.Reason, StringComparison.Ordinal);
    }

    private static RuntimeScriptExecutionRequest CreateRequest(
        string sourceCode,
        string? inputPayload = null)
    {
        return new RuntimeScriptExecutionRequest(
            CreateContext(),
            "Python",
            sourceCode,
            "7",
            inputPayload);
    }

    private static RuntimeCommandExecutionContext CreateContext()
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new ProductionRunId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
            OpenLineOps.Runtime.Domain.ProductionUnits.ProductionUnitId.New(),
            "line.main",
            "operation.main",
            "operation.main@0001",
            1,
            "station.main",
            new ProductionUnitIdentity("product.main", "serialNumber", "UNIT-001"),
            null,
            null,
            null,
            null,
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-normalize"),
            new RuntimeCapabilityId(RuntimeScriptCommand.PythonCapability),
            RuntimeScriptCommand.PythonCommandName,
            null,
            TimeSpan.FromSeconds(15),
            new RuntimeActionId("node-normalize:action:1"),
            "Capability",
            RuntimeScriptCommand.PythonCapability,
            "project.main",
            "application.main",
            "snapshot.release",
            RuntimeTestReleaseIdentity.ResourceFences());
    }
}
