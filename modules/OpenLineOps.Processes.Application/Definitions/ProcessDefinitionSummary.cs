namespace OpenLineOps.Processes.Application.Definitions;

public sealed record ProcessDefinitionSummary(
    string ProcessDefinitionId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc);
