namespace OpenLineOps.Processes.Application.Definitions;

public sealed record CreateProcessDefinitionRequest(
    string ProcessDefinitionId,
    string VersionId,
    string DisplayName,
    IReadOnlyCollection<CreateProcessNodeRequest>? Nodes,
    IReadOnlyCollection<CreateProcessTransitionRequest>? Transitions);
