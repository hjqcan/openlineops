namespace OpenLineOps.Engineering.Api.Models;

public sealed record ConfigurationSnapshotDiffItemResponse(
    string Area,
    string Field,
    string? FromValue,
    string? ToValue,
    string ChangeType);
