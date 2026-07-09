namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record ConfigurationSnapshotDiffItemDetails(
    string Area,
    string Field,
    string? FromValue,
    string? ToValue,
    string ChangeType);
