namespace OpenLineOps.Processes.Api.Models;

public sealed record CreateProcessDefinitionRequest(
    string? ProcessDefinitionId,
    string? VersionId,
    string? DisplayName,
    IReadOnlyCollection<CreateProcessNodeRequest>? Nodes,
    IReadOnlyCollection<CreateProcessTransitionRequest>? Transitions);
