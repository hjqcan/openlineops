namespace OpenLineOps.Engineering.Api.Models;

public sealed record ConfigurationSnapshotDiffResponse(
    string ProjectId,
    string FromSnapshotId,
    string ToSnapshotId,
    IReadOnlyCollection<ConfigurationSnapshotDiffItemResponse> Changes);
