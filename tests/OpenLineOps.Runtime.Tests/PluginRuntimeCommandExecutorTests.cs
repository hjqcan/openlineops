using OpenLineOps.Plugins.Application.Commands;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;
using OpenLineOps.Runtime.Infrastructure.Commands;

namespace OpenLineOps.Runtime.Tests;

public sealed class PluginRuntimeCommandExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncInvokesExactPluginCommandLockedByProjectRelease()
    {
        var resolver = new CapturingProjectReleasePluginCommandResolver(LockedVisionCommand());
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("{\"inspection\":\"pass\"}"));
        var executor = new PluginRuntimeCommandExecutor(resolver, invoker);

        var result = await executor.ExecuteAsync(CreateContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"inspection\":\"pass\"}", result.Payload);
        Assert.Equal("project.main", resolver.ProjectId);
        Assert.Equal("application.main", resolver.ApplicationId);
        Assert.Equal("snapshot.release", resolver.SnapshotId);
        Assert.Equal("System", resolver.TargetKind);
        Assert.Equal("system.vision", resolver.TargetId);

        var request = Assert.IsType<PluginProcessCommandInvocationRequest>(invoker.Request);
        Assert.Equal("openlineops.vision-process-plugin", request.PluginId);
        Assert.Equal("process.vision:inspect@2", request.CommandDefinitionId);
        Assert.Equal("2.3.4", request.PackageIdentity!.Version);
        Assert.Equal(new string('a', 64), request.PackageIdentity.PackageContentSha256);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsWhenImmutableReleaseHasNoExactCommand()
    {
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("should-not-run"));
        var executor = new PluginRuntimeCommandExecutor(
            new CapturingProjectReleasePluginCommandResolver(command: null),
            invoker);

        var result = await executor.ExecuteAsync(CreateContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("Immutable release", result.Reason, StringComparison.Ordinal);
        Assert.Null(invoker.Request);
    }

    [Fact]
    public async Task ExecuteAsyncRejectsIncompleteLockedPackageIdentity()
    {
        var invoker = new CapturingPluginProcessCommandInvoker(
            PluginProcessCommandInvocationResult.Completed("should-not-run"));
        var executor = new PluginRuntimeCommandExecutor(
            new CapturingProjectReleasePluginCommandResolver(
                LockedVisionCommand() with { PackageContentSha256 = string.Empty }),
            invoker);

        var result = await executor.ExecuteAsync(CreateContext());

        Assert.Equal(RuntimeCommandExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("incomplete", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(invoker.Request);
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
            new CapturingProjectReleasePluginCommandResolver(LockedVisionCommand()),
            invoker);

        var result = await executor.ExecuteAsync(CreateContext());

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal("terminal", result.Reason);
    }

    private static ProjectReleasePluginCommand LockedVisionCommand()
    {
        return new ProjectReleasePluginCommand(
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
            new RuntimeNodeId("node-inspect"),
            new RuntimeCapabilityId("process.vision"),
            "Inspect",
            "{\"serial\":\"ABC\"}",
            TimeSpan.FromSeconds(30),
            new RuntimeActionId("node-inspect:action:1"),
            "System",
            "system.vision",
            "project.main",
            "application.main",
            "snapshot.release",
            RuntimeTestReleaseIdentity.ResourceFences());
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

    private sealed class CapturingProjectReleasePluginCommandResolver(
        ProjectReleasePluginCommand? command) : IProjectReleasePluginCommandResolver
    {
        public string? ProjectId { get; private set; }

        public string? ApplicationId { get; private set; }

        public string? SnapshotId { get; private set; }

        public string? StationSystemId { get; private set; }

        public string? TargetKind { get; private set; }

        public string? TargetId { get; private set; }

        public ValueTask<ProjectReleasePluginCommand?> ResolveAsync(
            string projectId,
            string applicationId,
            string snapshotId,
            string stationSystemId,
            string capabilityId,
            string commandName,
            string? targetKind = null,
            string? targetId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectId = projectId;
            ApplicationId = applicationId;
            SnapshotId = snapshotId;
            StationSystemId = stationSystemId;
            TargetKind = targetKind;
            TargetId = targetId;
            return ValueTask.FromResult(command);
        }
    }
}
