using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Infrastructure.Scripting;
using OpenLineOps.ScriptWorker;

namespace OpenLineOps.Runtime.Tests;

public sealed class PythonScriptRuntimeScriptExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncRunsPythonSourceAndReturnsJsonResultPayload()
    {
        var executor = new PythonScriptRuntimeScriptExecutor();
        var request = CreateRequest(
            "result = {'normalized': input_payload, 'node': node_id}",
            inputPayload: "scan-ok");

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Payload);
        Assert.Contains("\"normalized\":\"scan-ok\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"node\":\"node-normalize\"", result.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsyncReturnsFailedWhenPythonSourceRaises()
    {
        var executor = new PythonScriptRuntimeScriptExecutor();
        var request = CreateRequest("raise Exception('script failed')");

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RuntimeCommandExecutionOutcome.Failed, result.Outcome);
        Assert.Contains("script failed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsUnsupportedLanguage()
    {
        var executor = new PythonScriptRuntimeScriptExecutor();
        var request = CreateRequest("result = 1", scriptLanguage: "JavaScript");

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("JavaScript", result.Reason, StringComparison.Ordinal);
    }

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
            result = {'normalized': input_payload, 'session': session_id}
            """,
            inputPayload: "scan-ok");

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Payload);
        Assert.Contains("\"normalized\":\"scan-ok\"", result.Payload, StringComparison.Ordinal);
        Assert.Contains("\"session\":\"00000000-0000-0000-0000-000000000001\"", result.Payload, StringComparison.Ordinal);
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
        string scriptLanguage = "Python",
        string? inputPayload = null)
    {
        return new RuntimeScriptExecutionRequest(
            CreateContext(),
            scriptLanguage,
            sourceCode,
            "7",
            inputPayload);
    }

    private static RuntimeCommandExecutionContext CreateContext()
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new StationId("station-a"),
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
            "snapshot.release");
    }
}
