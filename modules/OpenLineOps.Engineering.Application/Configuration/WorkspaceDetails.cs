namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record WorkspaceDetails(
    string WorkspaceId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc);
