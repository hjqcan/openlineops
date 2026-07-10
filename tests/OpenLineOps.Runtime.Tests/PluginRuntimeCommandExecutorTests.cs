using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Infrastructure.Commands;

namespace OpenLineOps.Runtime.Tests;

public sealed class PluginRuntimeCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncInvokesPluginProcessCommand()
    {
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("{\"inspection\":\"pass\"}"));
        var executor = new PluginRuntimeCommandExecutor(
            new InMemoryPluginProcessCommandInventory(VisionCommand),
            invoker);

        var result = await executor.ExecuteAsync(CreateContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"inspection\":\"pass\"}", result.Payload);
        Assert.NotNull(invoker.Request);
        Assert.Equal("openlineops.vision-process-plugin", invoker.Request.PluginId);
        Assert.Equal("00000000-0000-0000-0000-000000000001", invoker.Request.SessionId);
        Assert.Equal("station-a", invoker.Request.StationId);
        Assert.Equal("snapshot-20260629-001", invoker.Request.ConfigurationSnapshotId);
        Assert.Equal("00000000-0000-0000-0000-000000000002", invoker.Request.StepId);
        Assert.Equal("00000000-0000-0000-0000-000000000003", invoker.Request.CommandId);
        Assert.Equal("node-inspect", invoker.Request.NodeId);
        Assert.Equal("process.vision:inspect", invoker.Request.CommandDefinitionId);
        Assert.Equal("process.vision", invoker.Request.Capability);
        Assert.Equal("Inspect", invoker.Request.CommandName);
        Assert.Equal("{\"serial\":\"ABC\"}", invoker.Request.InputPayload);
        Assert.Equal(30000, invoker.Request.TimeoutMilliseconds);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsWhenNoPluginProcessCommandMatches()
    {
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("should-not-run"));
        var executor = new PluginRuntimeCommandExecutor(
            new InMemoryPluginProcessCommandInventory(),
            invoker);

        var result = await executor.ExecuteAsync(CreateContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("No plugin process command found", result.Reason, StringComparison.Ordinal);
        Assert.Null(invoker.Request);
    }

    [Fact]
    public async Task ExecuteAsyncProjectSnapshotFailsClosedWithoutReleaseResolver()
    {
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("should-not-run"));
        var executor = new PluginRuntimeCommandExecutor(
            new InMemoryPluginProcessCommandInventory(VisionCommand),
            invoker);

        var result = await executor.ExecuteAsync(CreateReleaseContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("verified release package resolver", result.Reason, StringComparison.Ordinal);
        Assert.Null(invoker.Request);
    }

    [Fact]
    public async Task ExecuteAsyncIncompleteProjectReleaseIdentityFailsClosedWithoutLiveInventoryFallback()
    {
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("should-not-run"));
        var executor = new PluginRuntimeCommandExecutor(
            new InMemoryPluginProcessCommandInventory(VisionCommand),
            invoker);

        var result = await executor.ExecuteAsync(CreateContext() with
        {
            ProjectId = "project.main"
        });

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("identity is incomplete", result.Reason, StringComparison.Ordinal);
        Assert.Null(invoker.Request);
    }

    [Fact]
    public async Task ExecuteAsyncProjectSnapshotUsesExactLockedPackageWithoutLiveInventoryFallback()
    {
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("locked"));
        var releaseCommand = new ProjectReleasePluginCommand(
            "openlineops.vision-process-plugin",
            "2.3.4",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "1.0.0",
            "win-x64",
            "openlineops.plugin-abi/1",
            "release/packages/locked",
            "process.vision:inspect@2",
            "process.vision",
            "Inspect");
        var executor = new PluginRuntimeCommandExecutor(
            new InMemoryPluginProcessCommandInventory(),
            invoker,
            new StaticProjectReleasePluginCommandResolver(releaseCommand));

        var result = await executor.ExecuteAsync(CreateReleaseContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.NotNull(invoker.Request);
        Assert.Equal("process.vision:inspect@2", invoker.Request.CommandDefinitionId);
        Assert.Equal("2.3.4", invoker.Request.PackageIdentity!.Version);
        Assert.Equal(new string('a', 64), invoker.Request.PackageIdentity.PackageContentSha256);
    }

    [Theory]
    [InlineData(PluginProcessCommandInvocationOutcome.Failed, RuntimeCommandExecutionOutcome.Failed)]
    [InlineData(PluginProcessCommandInvocationOutcome.Rejected, RuntimeCommandExecutionOutcome.Rejected)]
    [InlineData(PluginProcessCommandInvocationOutcome.TimedOut, RuntimeCommandExecutionOutcome.TimedOut)]
    [InlineData(PluginProcessCommandInvocationOutcome.Canceled, RuntimeCommandExecutionOutcome.Canceled)]
    public async Task ExecuteAsyncMapsTerminalOutcomes(
        PluginProcessCommandInvocationOutcome pluginOutcome,
        RuntimeCommandExecutionOutcome expectedOutcome)
    {
        var invoker = new CapturingPluginProcessCommandInvoker(new PluginProcessCommandInvocationResult(
            pluginOutcome,
            null,
            "terminal"));
        var executor = new PluginRuntimeCommandExecutor(
            new InMemoryPluginProcessCommandInventory(VisionCommand),
            invoker);

        var result = await executor.ExecuteAsync(CreateContext());

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal("terminal", result.Reason);
    }

    private static PluginProcessCommandDescriptor VisionCommand { get; } = new(
        "openlineops.vision-process-plugin",
        "Vision Process Plugin",
        OpenLineOps.Plugin.Abstractions.PluginKind.ProcessNode,
        "process.vision:inspect",
        "process.vision",
        "Inspect",
        InputSchema: null,
        OutputSchema: null,
        TimeoutMilliseconds: 30000,
        MaxRetries: 0);

    private static RuntimeCommandExecutionContext CreateContext()
    {
        return new RuntimeCommandExecutionContext(
            new RuntimeSessionId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new StationId("station-a"),
            new ConfigurationSnapshotId("snapshot-20260629-001"),
            new RuntimeStepId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            new RuntimeCommandId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
            new RuntimeNodeId("node-inspect"),
            new RuntimeCapabilityId("process.vision"),
            "Inspect",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30));
    }

    private static RuntimeCommandExecutionContext CreateReleaseContext()
    {
        return CreateContext() with
        {
            ProjectId = "project.main",
            ApplicationId = "application.main",
            ProjectSnapshotId = "snapshot.release"
        };
    }

    private sealed class InMemoryPluginProcessCommandInventory(
        params PluginProcessCommandDescriptor[] commands) : IPluginProcessCommandInventory
    {
        public ValueTask<IReadOnlyCollection<PluginProcessCommandDescriptor>> ListProcessCommandsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult<IReadOnlyCollection<PluginProcessCommandDescriptor>>(commands);
        }
    }

    private sealed class CapturingPluginProcessCommandInvoker(
        PluginProcessCommandInvocationResult result) : IPluginProcessCommandInvoker
    {
        public PluginProcessCommandInvocationRequest? Request { get; private set; }

        public ValueTask<PluginProcessCommandInvocationResult> ExecuteAsync(
            PluginProcessCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;

            return ValueTask.FromResult(result);
        }
    }

    private sealed class StaticProjectReleasePluginCommandResolver(
        ProjectReleasePluginCommand? command) : IProjectReleasePluginCommandResolver
    {
        public ValueTask<ProjectReleasePluginCommand?> ResolveAsync(
            string projectId,
            string applicationId,
            string snapshotId,
            string capabilityId,
            string commandName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(command);
        }
    }
}
