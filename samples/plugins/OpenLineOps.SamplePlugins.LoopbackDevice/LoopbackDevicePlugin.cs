using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.SamplePlugins.LoopbackDevice;

public sealed class LoopbackDevicePlugin : IOpenLineOpsDeviceCommandPlugin
{
    private const string Capability = "device.loopback";
    private const string EchoCommandName = "Echo";
    private const string EchoCommandDefinitionId = "loopback.echo";

    public PluginManifest Manifest { get; } = new(
        Id: "openlineops.samples.loopback-device",
        Name: "Loopback Device Sample",
        Version: "0.1.0",
        Kind: PluginKind.DeviceDriver,
        EntryAssembly: "OpenLineOps.SamplePlugins.LoopbackDevice.dll",
        EntryType: typeof(LoopbackDevicePlugin).FullName!,
        Capabilities: [Capability],
        DeviceCommands:
        [
            new PluginDeviceCommandDefinition(
                Id: EchoCommandDefinitionId,
                Capability: Capability,
                CommandName: EchoCommandName,
                InputSchema: "text/plain",
                OutputSchema: "application/json",
                TimeoutMilliseconds: 5000)
        ]);

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public ValueTask<PluginDeviceCommandExecutionResult> ExecuteDeviceCommandAsync(
        PluginDeviceCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!StringComparer.Ordinal.Equals(request.Capability, Capability))
        {
            return ValueTask.FromResult(
                PluginDeviceCommandExecutionResult.Rejected($"Unsupported capability '{request.Capability}'."));
        }

        if (!StringComparer.Ordinal.Equals(request.CommandName, EchoCommandName)
            || !StringComparer.Ordinal.Equals(request.CommandDefinitionId, EchoCommandDefinitionId))
        {
            return ValueTask.FromResult(
                PluginDeviceCommandExecutionResult.Rejected($"Unsupported command '{request.CommandName}'."));
        }

        var payload = request.InputPayload ?? string.Empty;
        var escapedPayload = payload.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var resultPayload = $$"""
            {"deviceInstanceId":"{{request.DeviceInstanceId}}","echo":"{{escapedPayload}}"}
            """;

        return ValueTask.FromResult(PluginDeviceCommandExecutionResult.Completed(resultPayload));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
