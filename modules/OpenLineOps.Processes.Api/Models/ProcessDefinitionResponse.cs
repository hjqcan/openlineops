namespace OpenLineOps.Processes.Api.Models;

public sealed record ProcessDefinitionResponse(
    string ProcessDefinitionId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    IReadOnlyCollection<ProcessNodeResponse> Nodes,
    IReadOnlyCollection<ProcessTransitionResponse> Transitions);
