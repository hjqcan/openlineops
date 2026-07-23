using System.Text.Json;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Commands;

namespace OpenLineOps.Runtime.Tests;

public sealed class RuntimeFlowCommandExecutorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExecuteAsyncCompletesZeroDurationWait()
    {
        var executor = new RuntimeFlowCommandExecutor();
        var payload = JsonSerializer.Serialize(new RuntimeFlowWaitCommandPayload(0), JsonOptions);

        var result = await executor.ExecuteAsync(CreateContext(payload, TimeSpan.FromSeconds(1)));

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(payload, result.Payload);
    }

    [Fact]
    public async Task ExecuteAsyncCompletesPositiveDurationWait()
    {
        var executor = new RuntimeFlowCommandExecutor();

        var result = await executor.ExecuteAsync(
            CreateContext("{\"durationMilliseconds\":1}", TimeSpan.FromSeconds(1)));

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
    }

    [Theory]
    [InlineData("{\"duration_ms\":0}")]
    [InlineData("{\"DURATION_MS\":0}")]
    [InlineData("{\"durationMilliseconds\":\"0\"}")]
    [InlineData("{\"durationMilliseconds\":0,\"extra\":true}")]
    public async Task ExecuteAsyncRejectsNonCanonicalDurationPayload(string payload)
    {
        var executor = new RuntimeFlowCommandExecutor();

        var result = await executor.ExecuteAsync(CreateContext(payload, TimeSpan.FromSeconds(1)));

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"durationMilliseconds\":-1}")]
    [InlineData("{\"durationMilliseconds\":\"Infinity\"}")]
    [InlineData("{\"durationMilliseconds\":4294967295}")]
    [InlineData("{not-json}")]
    public async Task ExecuteAsyncRejectsInvalidDuration(string? payload)
    {
        var executor = new RuntimeFlowCommandExecutor();

        var result = await executor.ExecuteAsync(CreateContext(payload, TimeSpan.FromSeconds(1)));

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task ExecuteAsyncReturnsTimedOutWhenCommandBudgetExpires()
    {
        var executor = new RuntimeFlowCommandExecutor();

        var result = await executor.ExecuteAsync(
            CreateContext("{\"durationMilliseconds\":5000}", TimeSpan.FromMilliseconds(10)));

        Assert.Equal(RuntimeCommandExecutionOutcome.TimedOut, result.Outcome);
        Assert.Equal("Runtime flow wait timed out.", result.Reason);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsTimeoutOutsideTimerRange()
    {
        var executor = new RuntimeFlowCommandExecutor();

        var result = await executor.ExecuteAsync(
            CreateContext("{\"durationMilliseconds\":0}", TimeSpan.FromDays(100)));

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("timer range", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsyncReturnsCanceledWhenExternalCancellationIsRequested()
    {
        var executor = new RuntimeFlowCommandExecutor();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        var result = await executor.ExecuteAsync(
            CreateContext("{\"durationMilliseconds\":5000}", TimeSpan.FromSeconds(5)),
            cancellation.Token);

        Assert.Equal(RuntimeCommandExecutionOutcome.Canceled, result.Outcome);
        Assert.Equal("Runtime flow wait was canceled.", result.Reason);
    }

    [Fact]
    public async Task ExecuteAsyncResolvesStaticResultPatchContext()
    {
        var executor = new RuntimeFlowCommandExecutor();
        var result = await executor.ExecuteAsync(CreateContext(
            """{"assignments":[{"key":"status","value":"ok"},{"key":"node","value":{"$context":"nodeId"}}]}""",
            TimeSpan.FromSeconds(1),
            RuntimeFlowCommand.ResultPatchCommandName));

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        using var payload = JsonDocument.Parse(result.Payload!);
        Assert.Equal("ok", payload.RootElement.GetProperty("status").GetString());
        Assert.Equal("node-wait", payload.RootElement.GetProperty("node").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"assignments\":{}}")]
    [InlineData("{\"assignments\":[{\"value\":1}]}")]
    public async Task ExecuteAsyncRejectsInvalidResultPatch(string payload)
    {
        var result = await new RuntimeFlowCommandExecutor().ExecuteAsync(CreateContext(
            payload,
            TimeSpan.FromSeconds(1),
            RuntimeFlowCommand.ResultPatchCommandName));

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
    }

    private static RuntimeCommandExecutionContext CreateContext(
        string? payload,
        TimeSpan timeout,
        string commandName = RuntimeFlowCommand.WaitCommandName)
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
            new RuntimeNodeId("node-wait"),
            new RuntimeCapabilityId(RuntimeFlowCommand.Capability),
            commandName,
            payload,
            timeout,
            new RuntimeActionId("node-wait:action:1"),
            "Capability",
            RuntimeFlowCommand.Capability,
            "project.main",
            "application.main",
            "snapshot.release",
            new Dictionary<string, ProductionContextValue>(),
            RuntimeTestReleaseIdentity.ResourceFences());
    }
}
