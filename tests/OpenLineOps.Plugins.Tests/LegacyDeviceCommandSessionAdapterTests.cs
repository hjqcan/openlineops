using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Tests;

public sealed class LegacyDeviceCommandSessionAdapterTests
{
    [Fact]
    public async Task ExactReplayReturnsCachedResultWithoutRepeatingNonIdempotentAction()
    {
        var legacy = new RecordingLegacyPlugin();
        await using var adapter = new LegacyDeviceCommandSessionAdapter(legacy);
        var session = await adapter.OpenAsync(new PluginDeviceSessionOpenRequest("fixture-01"));
        var command = Command(
            "command-001",
            fencingToken: 41,
            PluginDeviceCommandIdempotencyClass.NonIdempotent);
        var request = new PluginDeviceInvocationRequest(
            session.SessionId,
            command,
            "Clamp",
            """{"closed":true}""");

        var first = await adapter.InvokeAsync(request);
        var replay = await adapter.InvokeAsync(request);

        Assert.True(first.Succeeded);
        Assert.Equal(first, replay);
        Assert.Equal(1, legacy.ExecutionCount);
        Assert.Equal("fixture-01", legacy.LastRequest!.DeviceInstanceId);
        Assert.Equal("device.fixture:clamp", legacy.LastRequest.CommandDefinitionId);
    }

    [Fact]
    public async Task ConflictingReplayAndStaleFenceAreRejectedBeforeLegacyExecution()
    {
        var legacy = new RecordingLegacyPlugin();
        await using var adapter = new LegacyDeviceCommandSessionAdapter(legacy);
        var session = await adapter.OpenAsync(new PluginDeviceSessionOpenRequest("fixture-02"));

        var accepted = await adapter.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("command-002", 42, PluginDeviceCommandIdempotencyClass.Conditional),
            "Clamp",
            """{"closed":true}"""));
        var conflicting = await adapter.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("command-002", 42, PluginDeviceCommandIdempotencyClass.Conditional),
            "Clamp",
            """{"closed":false}"""));
        var stale = await adapter.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("command-003", 41, PluginDeviceCommandIdempotencyClass.Idempotent),
            "Clamp",
            """{"closed":false}"""));

        Assert.True(accepted.Succeeded);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, conflicting.Outcome);
        Assert.Contains("different evidence", conflicting.FailureReason, StringComparison.Ordinal);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, stale.Outcome);
        Assert.Contains("stale", stale.FailureReason, StringComparison.Ordinal);
        Assert.Equal(1, legacy.ExecutionCount);
    }

    [Fact]
    public async Task SafetyCriticalCommandIsNeverForwardedToLegacyContract()
    {
        var legacy = new RecordingLegacyPlugin();
        await using var adapter = new LegacyDeviceCommandSessionAdapter(legacy);
        var session = await adapter.OpenAsync(new PluginDeviceSessionOpenRequest("fixture-03"));
        var request = new PluginDeviceInvocationRequest(
            session.SessionId,
            new PluginDeviceCommandEnvelope(
                "command-safe",
                1,
                DateTimeOffset.UtcNow.AddMinutes(1),
                PluginDeviceCommandIdempotencyClass.NonIdempotent,
                PluginDeviceCommandSafetyClass.SafetyCritical),
            "Clamp");

        var result = await adapter.InvokeAsync(request);

        Assert.Equal(PluginDeviceOperationOutcome.Rejected, result.Outcome);
        Assert.Contains("safety-critical", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, legacy.ExecutionCount);
    }

    [Fact]
    public async Task CompatibilityHealthIsExplicitlyDegraded()
    {
        var legacy = new RecordingLegacyPlugin();
        await using var adapter = new LegacyDeviceCommandSessionAdapter(legacy);
        var session = await adapter.OpenAsync(new PluginDeviceSessionOpenRequest("fixture-04"));

        var health = await adapter.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));
        var diagnostics = await adapter.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.Equal(PluginDeviceHealthStatus.Degraded, health.Status);
        Assert.Equal("Plugin.LegacyCommandAdapter", Assert.Single(diagnostics.Entries).Code);
    }

    private static PluginDeviceCommandEnvelope Command(
        string commandId,
        long fencingToken,
        PluginDeviceCommandIdempotencyClass idempotencyClass) =>
        new(
            commandId,
            fencingToken,
            DateTimeOffset.UtcNow.AddMinutes(1),
            idempotencyClass,
            PluginDeviceCommandSafetyClass.Normal);

    private sealed class RecordingLegacyPlugin : IOpenLineOpsDeviceCommandPlugin
    {
        public int ExecutionCount { get; private set; }

        public PluginDeviceCommandExecutionRequest? LastRequest { get; private set; }

        public PluginManifest Manifest { get; } = new(
            "plugin.fixture.legacy",
            "Legacy fixture",
            "1.0.0",
            PluginKind.DeviceDriver,
            "Legacy.dll",
            "Legacy.Plugin",
            ["device.fixture"],
            DeviceCommands:
            [
                new PluginDeviceCommandDefinition(
                    "device.fixture:clamp",
                    "device.fixture",
                    "Clamp")
            ]);

        public ValueTask<PluginInitializationStatus> InitializeAsync(
            IServiceProvider services,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PluginInitializationStatus.Initialized);

        public ValueTask<PluginDeviceCommandExecutionResult> ExecuteDeviceCommandAsync(
            PluginDeviceCommandExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount += 1;
            LastRequest = request;
            return ValueTask.FromResult(
                PluginDeviceCommandExecutionResult.Completed("""{"accepted":true}"""));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
