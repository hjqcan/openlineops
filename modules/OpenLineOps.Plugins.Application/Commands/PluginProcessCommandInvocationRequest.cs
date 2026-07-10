using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Application.Commands;

public sealed record PluginProcessCommandInvocationRequest(
    string PluginId,
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
    int TimeoutMilliseconds,
    PluginPackageRuntimeIdentity? PackageIdentity = null);
