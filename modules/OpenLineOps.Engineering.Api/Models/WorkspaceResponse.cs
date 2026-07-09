namespace OpenLineOps.Engineering.Api.Models;

public sealed record WorkspaceResponse(
    string WorkspaceId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc);
