using OpenLineOps.Application.Abstractions.Paging;

namespace OpenLineOps.Traceability.Application.Queries;

public sealed record TraceRecordQuery
{
    public const int MaxPageSize = 500;

    public TraceRecordQuery(
        string? serialNumber = null,
        string? batchId = null,
        string? stationId = null,
        string? fixtureId = null,
        DateTimeOffset? completedFromUtc = null,
        DateTimeOffset? completedToUtc = null,
        PagedRequest? paging = null,
        string? processDefinitionId = null,
        string? processVersionId = null,
        string? configurationSnapshotId = null,
        string? recipeSnapshotId = null,
        string? deviceId = null,
        string? judgement = null,
        string? projectId = null,
        string? applicationId = null,
        string? projectSnapshotId = null,
        string? topologyId = null)
    {
        SerialNumber = NormalizeOptional(serialNumber);
        BatchId = NormalizeOptional(batchId);
        StationId = NormalizeOptional(stationId);
        FixtureId = NormalizeOptional(fixtureId);
        CompletedFromUtc = completedFromUtc;
        CompletedToUtc = completedToUtc;
        Paging = (paging ?? new PagedRequest()).Normalize(MaxPageSize);
        ProcessDefinitionId = NormalizeOptional(processDefinitionId);
        ProcessVersionId = NormalizeOptional(processVersionId);
        ConfigurationSnapshotId = NormalizeOptional(configurationSnapshotId);
        RecipeSnapshotId = NormalizeOptional(recipeSnapshotId);
        DeviceId = NormalizeOptional(deviceId);
        Judgement = NormalizeOptional(judgement);
        ProjectId = NormalizeOptional(projectId);
        ApplicationId = NormalizeOptional(applicationId);
        ProjectSnapshotId = NormalizeOptional(projectSnapshotId);
        TopologyId = NormalizeOptional(topologyId);
    }

    public string? SerialNumber { get; }

    public string? BatchId { get; }

    public string? StationId { get; }

    public string? FixtureId { get; }

    public DateTimeOffset? CompletedFromUtc { get; }

    public DateTimeOffset? CompletedToUtc { get; }

    public PagedRequest Paging { get; }

    public string? ProcessDefinitionId { get; }

    public string? ProcessVersionId { get; }

    public string? ConfigurationSnapshotId { get; }

    public string? RecipeSnapshotId { get; }

    public string? DeviceId { get; }

    public string? Judgement { get; }

    public string? ProjectId { get; }

    public string? ApplicationId { get; }

    public string? ProjectSnapshotId { get; }

    public string? TopologyId { get; }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
