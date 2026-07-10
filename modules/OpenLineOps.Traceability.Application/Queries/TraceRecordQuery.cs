using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Queries;

public sealed record TraceRecordQuery
{
    public const int MaxPageSize = 500;

    public TraceRecordQuery(
        Guid? productionRunId = null,
        string? dutModelId = null,
        string? dutIdentityInputKey = null,
        string? dutIdentityValue = null,
        string? batchId = null,
        string? fixtureId = null,
        string? deviceId = null,
        string? actorId = null,
        string? runStatus = null,
        string? judgement = null,
        string? projectId = null,
        string? applicationId = null,
        string? projectSnapshotId = null,
        string? topologyId = null,
        string? productionLineDefinitionId = null,
        string? stageId = null,
        string? workstationId = null,
        string? stationId = null,
        string? processDefinitionId = null,
        string? processVersionId = null,
        string? configurationSnapshotId = null,
        string? recipeSnapshotId = null,
        DateTimeOffset? completedFromUtc = null,
        DateTimeOffset? completedToUtc = null,
        PagedRequest? paging = null)
    {
        ProductionRunId = productionRunId;
        DutModelId = dutModelId;
        DutIdentityInputKey = dutIdentityInputKey;
        DutIdentityValue = dutIdentityValue;
        BatchId = batchId;
        FixtureId = fixtureId;
        DeviceId = deviceId;
        ActorId = actorId;
        RunStatus = runStatus;
        Judgement = judgement;
        ProjectId = projectId;
        ApplicationId = applicationId;
        ProjectSnapshotId = projectSnapshotId;
        TopologyId = topologyId;
        ProductionLineDefinitionId = productionLineDefinitionId;
        StageId = stageId;
        WorkstationId = workstationId;
        StationId = stationId;
        ProcessDefinitionId = processDefinitionId;
        ProcessVersionId = processVersionId;
        ConfigurationSnapshotId = configurationSnapshotId;
        RecipeSnapshotId = recipeSnapshotId;
        CompletedFromUtc = completedFromUtc;
        CompletedToUtc = completedToUtc;
        Paging = (paging ?? new PagedRequest()).Normalize(MaxPageSize);
    }

    public Guid? ProductionRunId { get; }
    public string? DutModelId { get; }
    public string? DutIdentityInputKey { get; }
    public string? DutIdentityValue { get; }
    public string? BatchId { get; }
    public string? FixtureId { get; }
    public string? DeviceId { get; }
    public string? ActorId { get; }
    public string? RunStatus { get; }
    public string? Judgement { get; }
    public string? ProjectId { get; }
    public string? ApplicationId { get; }
    public string? ProjectSnapshotId { get; }
    public string? TopologyId { get; }
    public string? ProductionLineDefinitionId { get; }
    public string? StageId { get; }
    public string? WorkstationId { get; }
    public string? StationId { get; }
    public string? ProcessDefinitionId { get; }
    public string? ProcessVersionId { get; }
    public string? ConfigurationSnapshotId { get; }
    public string? RecipeSnapshotId { get; }
    public DateTimeOffset? CompletedFromUtc { get; }
    public DateTimeOffset? CompletedToUtc { get; }
    public PagedRequest Paging { get; }

    public ApplicationError? Validate()
    {
        if (ProductionRunId == Guid.Empty)
        {
            return ApplicationError.Validation(
                "Traceability.ProductionRunIdInvalid",
                "ProductionRunId cannot be an empty GUID.");
        }

        if (CompletedFromUtc is not null
            && CompletedToUtc is not null
            && CompletedToUtc < CompletedFromUtc)
        {
            return ApplicationError.Validation(
                "Traceability.InvalidTimeRange",
                "CompletedToUtc cannot be earlier than CompletedFromUtc.");
        }

        foreach (var (name, value) in TextFilters())
        {
            if (value is not null
                && (string.IsNullOrWhiteSpace(value)
                    || char.IsWhiteSpace(value[0])
                    || char.IsWhiteSpace(value[^1])))
            {
                return ApplicationError.Validation(
                    $"Traceability.{name}NotCanonical",
                    $"{name} must be null or a non-empty canonical string.");
            }
        }

        if (RunStatus is not null
            && !CanonicalEnumToken.TryParse<TraceProductionRunStatus>(RunStatus, out _))
        {
            return ApplicationError.Validation(
                "Traceability.InvalidRunStatus",
                $"RunStatus must be an exact, case-sensitive token: "
                + CanonicalEnumToken.ExpectedTokens<TraceProductionRunStatus>());
        }

        if (Judgement is not null
            && !CanonicalEnumToken.TryParse<ResultJudgement>(Judgement, out _))
        {
            return ApplicationError.Validation(
                "Traceability.InvalidJudgement",
                $"Judgement must be an exact, case-sensitive token: "
                + CanonicalEnumToken.ExpectedTokens<ResultJudgement>());
        }

        return null;
    }

    private IEnumerable<(string Name, string? Value)> TextFilters()
    {
        yield return (nameof(DutModelId), DutModelId);
        yield return (nameof(DutIdentityInputKey), DutIdentityInputKey);
        yield return (nameof(DutIdentityValue), DutIdentityValue);
        yield return (nameof(BatchId), BatchId);
        yield return (nameof(FixtureId), FixtureId);
        yield return (nameof(DeviceId), DeviceId);
        yield return (nameof(ActorId), ActorId);
        yield return (nameof(RunStatus), RunStatus);
        yield return (nameof(Judgement), Judgement);
        yield return (nameof(ProjectId), ProjectId);
        yield return (nameof(ApplicationId), ApplicationId);
        yield return (nameof(ProjectSnapshotId), ProjectSnapshotId);
        yield return (nameof(TopologyId), TopologyId);
        yield return (nameof(ProductionLineDefinitionId), ProductionLineDefinitionId);
        yield return (nameof(StageId), StageId);
        yield return (nameof(WorkstationId), WorkstationId);
        yield return (nameof(StationId), StationId);
        yield return (nameof(ProcessDefinitionId), ProcessDefinitionId);
        yield return (nameof(ProcessVersionId), ProcessVersionId);
        yield return (nameof(ConfigurationSnapshotId), ConfigurationSnapshotId);
        yield return (nameof(RecipeSnapshotId), RecipeSnapshotId);
    }
}
