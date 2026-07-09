namespace OpenLineOps.Processes.Api.Models;

public sealed record ProcessDefinitionSummaryResponse(
    string ProcessDefinitionId,
    string VersionId,
    string DisplayName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc);
