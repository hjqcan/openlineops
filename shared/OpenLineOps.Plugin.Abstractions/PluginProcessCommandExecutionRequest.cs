namespace OpenLineOps.Plugin.Abstractions;

public sealed record PluginProcessCommandExecutionRequest(
    string SessionId,
    string StationId,
    string ConfigurationSnapshotId,
    string StepId,
    string CommandId,
    string NodeId,
    string CommandDefinitionId,
    string Capability,
    string CommandName,
    string? InputPayload,
    TimeSpan Timeout);
