namespace OpenLineOps.Runtime.Api.Models;

public sealed record StartSimulatedRuntimeNodeRequest(
    string NodeId,
    string DisplayName,
    string TargetCapability,
    string CommandName,
    int TimeoutSeconds,
    string? InputPayload);
