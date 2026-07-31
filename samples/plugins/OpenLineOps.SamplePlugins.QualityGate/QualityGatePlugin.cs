using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.SamplePlugins.QualityGate;

public sealed class QualityGatePlugin : IOpenLineOpsProcessNodePlugin
{
    private const string Capability = "production.quality-gate";
    private const string CommandName = "Evaluate";
    private const string CommandDefinitionId = "quality-gate.evaluate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PluginManifest Manifest { get; } = new(
        Id: "openlineops.samples.quality-gate",
        Name: "Quality Gate Sample",
        Version: "1.0.0",
        Kind: PluginKind.ProcessNode,
        EntryAssembly: "OpenLineOps.SamplePlugins.QualityGate.dll",
        EntryType: typeof(QualityGatePlugin).FullName!,
        Capabilities: [Capability],
        ProcessCommands:
        [
            new PluginProcessCommandDefinition(
                Id: CommandDefinitionId,
                Capability: Capability,
                CommandName: CommandName,
                InputSchema: "application/json",
                OutputSchema: "application/json",
                TimeoutMilliseconds: 5000)
        ]);

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public ValueTask<PluginProcessCommandExecutionResult> ExecuteProcessCommandAsync(
        PluginProcessCommandExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!StringComparer.Ordinal.Equals(request.Capability, Capability)
            || !StringComparer.Ordinal.Equals(request.CommandName, CommandName)
            || !StringComparer.Ordinal.Equals(request.CommandDefinitionId, CommandDefinitionId))
        {
            return ValueTask.FromResult(
                PluginProcessCommandExecutionResult.Rejected(
                    $"Unsupported quality-gate command '{request.CommandDefinitionId}'."));
        }

        QualityGateInput? input;
        try
        {
            input = JsonSerializer.Deserialize<QualityGateInput>(request.InputPayload ?? "", JsonOptions);
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(
                PluginProcessCommandExecutionResult.Failed(
                    "Quality-gate input must be valid JSON."));
        }

        if (input is null || input.ResultJudgement is not ("Passed" or "Failed" or "Aborted"))
        {
            return ValueTask.FromResult(
                PluginProcessCommandExecutionResult.Failed(
                    "Quality-gate resultJudgement must be Passed, Failed, or Aborted."));
        }

        var payload = JsonSerializer.Serialize(
            new QualityGateResult(
                ExecutionStatus: "Completed",
                ResultJudgement: input.ResultJudgement,
                Detail: input.Detail),
            JsonOptions);
        return ValueTask.FromResult(PluginProcessCommandExecutionResult.Completed(payload));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record QualityGateInput(
        string ResultJudgement,
        string? Detail);

    private sealed record QualityGateResult(
        string ExecutionStatus,
        string ResultJudgement,
        string? Detail);
}
