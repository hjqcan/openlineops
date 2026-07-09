namespace OpenLineOps.Processes.Application.Definitions;

public sealed record ProcessDefinitionDetails(
    string ProcessDefinitionId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    IReadOnlyCollection<ProcessNodeDetails> Nodes,
    IReadOnlyCollection<ProcessTransitionDetails> Transitions);
