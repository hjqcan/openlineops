using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Traceability.Application.Queries;

public sealed record TraceRecordQuery
{
    public const int MaxPageSize = 500;

    public TraceRecordQuery(
        Guid? productionRunId = null,
        string? productModelId = null,
        string? productionUnitIdentityInputKey = null,
        string? productionUnitIdentityValue = null,
        string? lotId = null,
        string? carrierId = null,
        string? actorId = null,
        string? executionStatus = null,
        string? judgement = null,
        string? disposition = null,
        string? projectId = null,
        string? applicationId = null,
        string? projectSnapshotId = null,
        string? topologyId = null,
        string? productionLineDefinitionId = null,
        string? operationId = null,
        string? stationSystemId = null,
        string? stationId = null,
        string? processDefinitionId = null,
        string? processVersionId = null,
        string? configurationSnapshotId = null,
        string? recipeSnapshotId = null,
        string? resourceKind = null,
        string? resourceId = null,
        string? deviceId = null,
        DateTimeOffset? completedFromUtc = null,
        DateTimeOffset? completedToUtc = null,
        PagedRequest? paging = null)
    {
        ProductionRunId = productionRunId;
        ProductModelId = productModelId;
        ProductionUnitIdentityInputKey = productionUnitIdentityInputKey;
        ProductionUnitIdentityValue = productionUnitIdentityValue;
        LotId = lotId;
        CarrierId = carrierId;
        ActorId = actorId;
        ExecutionStatus = executionStatus;
        Judgement = judgement;
        Disposition = disposition;
        ProjectId = projectId;
        ApplicationId = applicationId;
        ProjectSnapshotId = projectSnapshotId;
        TopologyId = topologyId;
        ProductionLineDefinitionId = productionLineDefinitionId;
        OperationId = operationId;
        StationSystemId = stationSystemId;
        StationId = stationId;
        ProcessDefinitionId = processDefinitionId;
        ProcessVersionId = processVersionId;
        ConfigurationSnapshotId = configurationSnapshotId;
        RecipeSnapshotId = recipeSnapshotId;
        ResourceKind = resourceKind;
        ResourceId = resourceId;
        DeviceId = deviceId;
        CompletedFromUtc = completedFromUtc;
        CompletedToUtc = completedToUtc;
        Paging = (paging ?? new PagedRequest()).Normalize(MaxPageSize);
    }

    public Guid? ProductionRunId { get; }
    public string? ProductModelId { get; }
    public string? ProductionUnitIdentityInputKey { get; }
    public string? ProductionUnitIdentityValue { get; }
    public string? LotId { get; }
    public string? CarrierId { get; }
    public string? ActorId { get; }
    public string? ExecutionStatus { get; }
    public string? Judgement { get; }
    public string? Disposition { get; }
    public string? ProjectId { get; }
    public string? ApplicationId { get; }
    public string? ProjectSnapshotId { get; }
    public string? TopologyId { get; }
    public string? ProductionLineDefinitionId { get; }
    public string? OperationId { get; }
    public string? StationSystemId { get; }
    public string? StationId { get; }
    public string? ProcessDefinitionId { get; }
    public string? ProcessVersionId { get; }
    public string? ConfigurationSnapshotId { get; }
    public string? RecipeSnapshotId { get; }
    public string? ResourceKind { get; }
    public string? ResourceId { get; }
    public string? DeviceId { get; }
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

        var executionStatusError = ValidateEnum<ExecutionStatus>(ExecutionStatus, nameof(ExecutionStatus));
        if (executionStatusError is not null)
        {
            return executionStatusError;
        }

        var judgementError = ValidateEnum<ResultJudgement>(Judgement, nameof(Judgement));
        if (judgementError is not null)
        {
            return judgementError;
        }

        return ValidateEnum<ProductDisposition>(Disposition, nameof(Disposition));
    }

    private static ApplicationError? ValidateEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        return value is not null && !CanonicalEnumToken.TryParse<TEnum>(value, out _)
            ? ApplicationError.Validation(
                $"Traceability.Invalid{fieldName}",
                $"{fieldName} must be an exact, case-sensitive token: "
                + CanonicalEnumToken.ExpectedTokens<TEnum>())
            : null;
    }

    private IEnumerable<(string Name, string? Value)> TextFilters()
    {
        yield return (nameof(ProductModelId), ProductModelId);
        yield return (nameof(ProductionUnitIdentityInputKey), ProductionUnitIdentityInputKey);
        yield return (nameof(ProductionUnitIdentityValue), ProductionUnitIdentityValue);
        yield return (nameof(LotId), LotId);
        yield return (nameof(CarrierId), CarrierId);
        yield return (nameof(ActorId), ActorId);
        yield return (nameof(ExecutionStatus), ExecutionStatus);
        yield return (nameof(Judgement), Judgement);
        yield return (nameof(Disposition), Disposition);
        yield return (nameof(ProjectId), ProjectId);
        yield return (nameof(ApplicationId), ApplicationId);
        yield return (nameof(ProjectSnapshotId), ProjectSnapshotId);
        yield return (nameof(TopologyId), TopologyId);
        yield return (nameof(ProductionLineDefinitionId), ProductionLineDefinitionId);
        yield return (nameof(OperationId), OperationId);
        yield return (nameof(StationSystemId), StationSystemId);
        yield return (nameof(StationId), StationId);
        yield return (nameof(ProcessDefinitionId), ProcessDefinitionId);
        yield return (nameof(ProcessVersionId), ProcessVersionId);
        yield return (nameof(ConfigurationSnapshotId), ConfigurationSnapshotId);
        yield return (nameof(RecipeSnapshotId), RecipeSnapshotId);
        yield return (nameof(ResourceKind), ResourceKind);
        yield return (nameof(ResourceId), ResourceId);
        yield return (nameof(DeviceId), DeviceId);
    }
}
