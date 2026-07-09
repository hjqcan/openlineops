namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record ConfigurationSnapshotDiffDetails(
    string ProjectId,
    string FromSnapshotId,
    string ToSnapshotId,
    IReadOnlyCollection<ConfigurationSnapshotDiffItemDetails> Changes);
