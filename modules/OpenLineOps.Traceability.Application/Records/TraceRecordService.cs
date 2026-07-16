using System.Text.Json;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Records;

public sealed class TraceRecordService : ITraceRecordService
{
    private const string PackageFormat = "openlineops.production-run-trace-package";

    private readonly ITraceRecordRepository _repository;
    private readonly IClock _clock;

    public TraceRecordService(
        ITraceRecordRepository repository,
        IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<Result<TraceRecordDetails>> CreateAsync(
        CreateTraceRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationError = ValidateCreateRequest(request);
        if (validationError is not null)
        {
            return Result.Failure<TraceRecordDetails>(validationError);
        }

        try
        {
            var runId = new ProductionRunId(request.ProductionRunId);
            var productionUnitId = new ProductionUnitId(request.ProductionUnitId);
            var traceRecord = TraceRecord.Create(
                new TraceRecordId(runId.Value),
                runId,
                productionUnitId,
                request.ProjectId!,
                request.ApplicationId!,
                request.ProjectSnapshotId!,
                request.TopologyId!,
                request.ProductionLineDefinitionId!,
                request.ProductModelId!,
                request.ProductionUnitIdentityInputKey!,
                request.ProductionUnitIdentityValue!,
                request.LotId,
                request.CarrierId,
                new ActorId(request.ActorId!),
                ParseEnum<ExecutionStatus>(request.ExecutionStatus!, "Traceability.InvalidExecutionStatus"),
                ParseEnum<ResultJudgement>(request.Judgement!, "Traceability.InvalidJudgement"),
                ParseEnum<ProductDisposition>(request.Disposition!, "Traceability.InvalidDisposition"),
                request.CreatedAtUtc,
                request.StartedAtUtc,
                request.CompletedAtUtc,
                request.FailureCode,
                request.FailureReason,
                request.Operations!.Select(ToOperation),
                (request.RouteDecisions ?? []).Select(ToRouteDecision),
                (request.Genealogy ?? []).Select(ToGenealogy),
                (request.MaterialLocationTransitions ?? []).Select(ToMaterialLocationTransition),
                (request.SlotOccupancyTransitions ?? []).Select(ToSlotOccupancyTransition),
                (request.DispositionTransitions ?? []).Select(ToDispositionTransition),
                (request.AuditEntries ?? []).Select(ToAuditEntry));

            var added = await _repository
                .TryAddAsync(traceRecord, cancellationToken)
                .ConfigureAwait(false);
            if (added)
            {
                return Result.Success(TraceRecordMapper.ToDetails(traceRecord));
            }

            var existing = await _repository
                .GetByIdAsync(traceRecord.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null || !HasIdenticalEvidence(existing, traceRecord))
            {
                return Result.Failure<TraceRecordDetails>(ApplicationError.Conflict(
                    "Traceability.RecordEvidenceConflict",
                    $"Production run {runId} already has different immutable trace evidence."));
            }

            return Result.Failure<TraceRecordDetails>(ApplicationError.Conflict(
                "Traceability.RecordAlreadyExists",
                $"Trace record for production run {runId} already exists with identical evidence."));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return InvalidRecord(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return InvalidRecord(exception.Message);
        }
    }

    public async Task<Result<TraceRecordDetails>> GetByIdAsync(
        Guid traceRecordId,
        CancellationToken cancellationToken = default)
    {
        if (traceRecordId == Guid.Empty)
        {
            return Result.Failure<TraceRecordDetails>(InvalidTraceRecordId());
        }

        var traceRecord = await _repository
            .GetByIdAsync(new TraceRecordId(traceRecordId), cancellationToken)
            .ConfigureAwait(false);
        return traceRecord is null
            ? Result.Failure<TraceRecordDetails>(NotFound(traceRecordId))
            : Result.Success(TraceRecordMapper.ToDetails(traceRecord));
    }

    public async Task<Result<PagedResult<TraceRecordSummary>>> QueryAsync(
        TraceRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var validationError = query.Validate();
        if (validationError is not null)
        {
            return Result.Failure<PagedResult<TraceRecordSummary>>(validationError);
        }

        var result = await _repository.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        return Result.Success(new PagedResult<TraceRecordSummary>(
            result.Items.Select(TraceRecordMapper.ToSummary).ToArray(),
            result.PageNumber,
            result.PageSize,
            result.TotalCount));
    }

    public async Task<Result<TraceRecordExportPackage>> ExportAsync(
        Guid traceRecordId,
        CancellationToken cancellationToken = default)
    {
        var details = await GetByIdAsync(traceRecordId, cancellationToken).ConfigureAwait(false);
        return details.IsFailure
            ? Result.Failure<TraceRecordExportPackage>(details.Error)
            : Result.Success(new TraceRecordExportPackage(
                PackageFormat,
                _clock.UtcNow,
                details.Value));
    }

    private static TraceOperationExecution ToOperation(CreateTraceOperationExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new TraceOperationExecution(
            request.OperationRunId!,
            request.OperationId!,
            request.Attempt,
            request.StationSystemId!,
            new StationId(request.StationId!),
            new ProcessDefinitionId(request.ProcessDefinitionId!),
            new ProcessVersionId(request.ProcessVersionId!),
            new ConfigurationSnapshotId(request.ConfigurationSnapshotId!),
            new RecipeSnapshotId(request.RecipeSnapshotId!),
            request.RuntimeSessionId is null ? null : new RuntimeSessionId(request.RuntimeSessionId.Value),
            request.RuntimeSessionStatus is null
                ? null
                : ParseEnum<TraceRuntimeSessionStatus>(
                    request.RuntimeSessionStatus,
                    "Traceability.InvalidRuntimeSessionStatus"),
            ParseEnum<ExecutionStatus>(
                request.ExecutionStatus!,
                "Traceability.InvalidOperationExecutionStatus"),
            ParseEnum<ResultJudgement>(request.Judgement!, "Traceability.InvalidOperationJudgement"),
            request.StartedAtUtc,
            request.CompletedAtUtc,
            request.FailureCode,
            request.FailureReason,
            request.CompletedStepCount,
            request.CommandCount,
            request.IncidentCount,
            (request.Commands ?? []).Select(ToCommand),
            (request.Measurements ?? []).Select(ToMeasurement),
            (request.Artifacts ?? []).Select(ToArtifact),
            (request.Incidents ?? []).Select(ToIncident),
            (request.Outputs ?? []).Select(ToOutput),
            (request.FencingTokens ?? []).Select(ToFencingToken));
    }

    private static TraceRouteDecision ToRouteDecision(CreateTraceRouteDecisionRequest request) => new(
        request.SourceOperationRunId!,
        request.TransitionId!,
        request.TargetOperationId,
        request.TerminalDisposition is null
            ? null
            : ParseEnum<ProductDisposition>(
                request.TerminalDisposition,
                "Traceability.InvalidRouteTerminalDisposition"),
        ParseEnum<ResultJudgement>(
            request.SourceJudgement!,
            "Traceability.InvalidRouteSourceJudgement"),
        request.Traversal,
        request.DecidedAtUtc);

    private static TraceMaterialGenealogy ToGenealogy(CreateTraceMaterialGenealogyRequest request) =>
        new(
            request.LinkId,
            request.ParentProductionUnitId,
            request.ChildProductionUnitId,
            request.Relationship!,
            request.OperationId!,
            request.LinkedBy!,
            request.LinkedAtUtc);

    private static TraceMaterialLocationTransition ToMaterialLocationTransition(
        CreateTraceMaterialLocationTransitionRequest request) => new(
        request.EvidenceId,
        request.ProductionRunId,
        request.MaterialKind!,
        request.MaterialId!,
        request.Source is null ? null : ToMaterialLocation(request.Source),
        ToMaterialLocation(request.Destination!),
        request.ActorId!,
        request.OccurredAtUtc);

    private static TraceMaterialLocation ToMaterialLocation(
        CreateTraceMaterialLocationRequest request) => new(
        request.Kind!,
        request.LineId,
        request.StationSystemId,
        request.SlotId,
        request.CarrierId,
        request.CarrierPositionId);

    private static TraceSlotOccupancyTransition ToSlotOccupancyTransition(
        CreateTraceSlotOccupancyTransitionRequest request) => new(
        request.EvidenceId,
        request.ProductionRunId,
        request.LineId!,
        request.StationSystemId!,
        request.SlotId!,
        request.MaterialKind!,
        request.MaterialId!,
        request.PreviousStatus!,
        request.CurrentStatus!,
        request.ActorId!,
        request.OccurredAtUtc);

    private static TraceDispositionTransition ToDispositionTransition(
        CreateTraceDispositionTransitionRequest request) => new(
        request.EvidenceId,
        request.ProductionUnitId,
        request.ProductionRunId,
        ParseEnum<ProductDisposition>(
            request.PreviousDisposition!,
            "Traceability.InvalidPreviousDisposition"),
        ParseEnum<ProductDisposition>(
            request.CurrentDisposition!,
            "Traceability.InvalidCurrentDisposition"),
        request.Reason,
        request.ActorId!,
        request.OccurredAtUtc);

    private static TraceOperationOutput ToOutput(CreateTraceOperationOutputRequest request) => new(
        request.Key!,
        request.ValueKind!,
        request.CanonicalJson!);

    private static TraceResourceFencingToken ToFencingToken(
        CreateTraceResourceFencingTokenRequest request) => new(
        request.ResourceKind!,
        request.ResourceId!,
        request.FencingToken);

    private static TraceCommandRecord ToCommand(CreateTraceCommandRequest request)
    {
        return new TraceCommandRecord(
            new RuntimeCommandId(request.RuntimeCommandId),
            request.RuntimeStepId,
            request.ActionId!,
            ParseEnum<TraceTargetKind>(request.TargetKind!, "Traceability.InvalidTargetKind"),
            request.TargetId!,
            request.TargetCapabilityId!,
            request.CommandName!,
            ParseEnum<ExecutionStatus>(
                request.ExecutionStatus!,
                "Traceability.InvalidCommandExecutionStatus"),
            ParseEnum<ResultJudgement>(
                request.ResultJudgement!,
                "Traceability.InvalidCommandResultJudgement"),
            request.CreatedAtUtc,
            request.DeadlineAtUtc,
            request.AcceptedAtUtc,
            request.StartedAtUtc,
            request.CompletedAtUtc,
            request.ResultPayload,
            request.FailureReason);
    }

    private static MeasurementRecord ToMeasurement(CreateMeasurementRecordRequest request)
    {
        return new MeasurementRecord(
            request.MeasurementRecordId is null
                ? MeasurementRecordId.New()
                : new MeasurementRecordId(request.MeasurementRecordId.Value),
            request.Name!,
            request.NumericValue,
            request.TextValue,
            request.Unit,
            request.DeviceId is null ? null : new DeviceId(request.DeviceId),
            request.RuntimeCommandId is null ? null : new RuntimeCommandId(request.RuntimeCommandId.Value),
            request.ActionId!,
            ParseEnum<TraceTargetKind>(request.TargetKind!, "Traceability.InvalidTargetKind"),
            request.TargetId!,
            ParseEnum<ExecutionStatus>(
                request.CommandExecutionStatus!,
                "Traceability.InvalidCommandExecutionStatus"),
            ParseEnum<ResultJudgement>(
                request.CommandResultJudgement!,
                "Traceability.InvalidCommandResultJudgement"),
            request.Passed,
            request.MeasuredAtUtc);
    }

    private static ArtifactRecord ToArtifact(CreateArtifactRecordRequest request)
    {
        return new ArtifactRecord(
            request.ArtifactRecordId is null
                ? ArtifactRecordId.New()
                : new ArtifactRecordId(request.ArtifactRecordId.Value),
            request.Name!,
            ParseEnum<ArtifactKind>(request.Kind!, "Traceability.InvalidArtifactKind"),
            request.StorageKey!,
            request.MediaType,
            request.SizeBytes,
            request.Sha256,
            request.DeviceId is null ? null : new DeviceId(request.DeviceId),
            request.CapturedAtUtc);
    }

    private static TraceIncidentRecord ToIncident(CreateTraceIncidentRequest request)
    {
        return new TraceIncidentRecord(
            request.RuntimeIncidentId,
            ParseEnum<TraceIncidentSeverity>(
                request.Severity!,
                "Traceability.InvalidIncidentSeverity"),
            request.Code!,
            request.Message!,
            request.OccurredAtUtc);
    }

    private static AuditEntry ToAuditEntry(CreateAuditEntryRequest request)
    {
        return new AuditEntry(
            request.AuditEntryId is null ? AuditEntryId.New() : new AuditEntryId(request.AuditEntryId.Value),
            new ActorId(request.ActorId!),
            request.Action!,
            request.Detail,
            request.OccurredAtUtc);
    }

    private static ApplicationError? ValidateCreateRequest(CreateTraceRecordRequest request)
    {
        if (request.ProductionRunId == Guid.Empty)
        {
            return ApplicationError.Validation(
                "Traceability.ProductionRunIdRequired",
                "ProductionRunId is required.");
        }

        if (request.ProductionUnitId == Guid.Empty)
        {
            return ApplicationError.Validation(
                "Traceability.ProductionUnitIdRequired",
                "ProductionUnitId is required.");
        }

        if (request.CreatedAtUtc == default || request.CompletedAtUtc == default)
        {
            return ApplicationError.Validation(
                "Traceability.RunTimestampsRequired",
                "CreatedAtUtc and CompletedAtUtc are required.");
        }

        if (request.StartedAtUtc < request.CreatedAtUtc
            || request.CompletedAtUtc < (request.StartedAtUtc ?? request.CreatedAtUtc))
        {
            return ApplicationError.Validation(
                "Traceability.InvalidRunTimeRange",
                "Production run timestamps must be chronological.");
        }

        var requiredError = RequiredCanonical(request.ProjectId, "ProjectId")
            ?? RequiredCanonical(request.ApplicationId, "ApplicationId")
            ?? RequiredCanonical(request.ProjectSnapshotId, "ProjectSnapshotId")
            ?? RequiredCanonical(request.TopologyId, "TopologyId")
            ?? RequiredCanonical(request.ProductionLineDefinitionId, "ProductionLineDefinitionId")
            ?? RequiredCanonical(request.ProductModelId, "ProductModelId")
            ?? RequiredCanonical(
                request.ProductionUnitIdentityInputKey,
                "ProductionUnitIdentityInputKey")
            ?? RequiredCanonical(
                request.ProductionUnitIdentityValue,
                "ProductionUnitIdentityValue")
            ?? RequiredCanonical(request.ActorId, "ActorId")
            ?? RequiredCanonical(request.ExecutionStatus, "ExecutionStatus")
            ?? RequiredCanonical(request.Judgement, "Judgement")
            ?? RequiredCanonical(request.Disposition, "Disposition")
            ?? OptionalCanonical(request.LotId, "LotId")
            ?? OptionalCanonical(request.CarrierId, "CarrierId")
            ?? OptionalCanonical(request.FailureCode, "FailureCode")
            ?? OptionalCanonical(request.FailureReason, "FailureReason");
        if (requiredError is not null)
        {
            return requiredError;
        }

        if (request.Operations is null)
        {
            return ApplicationError.Validation(
                "Traceability.OperationsRequired",
                "Operations are required, including an empty collection for a pre-start cancellation.");
        }

        if (request.Operations.Count == 0
            && (!string.Equals(
                    request.ExecutionStatus,
                    ExecutionStatus.Canceled.ToString(),
                    StringComparison.Ordinal)
                || request.StartedAtUtc is not null))
        {
            return ApplicationError.Validation(
                "Traceability.OperationsRequired",
                "Only a Production Run canceled before execution may contain no Operations.");
        }

        return null;
    }

    private static ApplicationError? RequiredCanonical(string? value, string fieldName)
    {
        return value is null || string.IsNullOrWhiteSpace(value)
            ? ApplicationError.Validation($"Traceability.{fieldName}Required", $"{fieldName} is required.")
            : !IsCanonical(value)
                ? ApplicationError.Validation(
                    $"Traceability.{fieldName}NotCanonical",
                    $"{fieldName} must not contain leading or trailing whitespace.")
                : null;
    }

    private static ApplicationError? OptionalCanonical(string? value, string fieldName)
    {
        return value is not null && !IsCanonical(value)
            ? ApplicationError.Validation(
                $"Traceability.{fieldName}NotCanonical",
                $"{fieldName} must be null or a non-empty canonical string.")
            : null;
    }

    private static bool IsCanonical(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[^1]);
    }

    private static TEnum ParseEnum<TEnum>(string value, string errorCode)
        where TEnum : struct, Enum
    {
        if (CanonicalEnumToken.TryParse<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"{errorCode}: '{value}' is not supported. Expected an exact, case-sensitive token: "
            + CanonicalEnumToken.ExpectedTokens<TEnum>());
    }

    private static TEnum? ParseOptionalEnum<TEnum>(string? value, string errorCode)
        where TEnum : struct, Enum
    {
        return value is null ? null : ParseEnum<TEnum>(value, errorCode);
    }

    private static Result<TraceRecordDetails> InvalidRecord(string message)
    {
        return Result.Failure<TraceRecordDetails>(ApplicationError.Validation(
            "Traceability.InvalidRecordInput",
            message));
    }

    private static bool HasIdenticalEvidence(TraceRecord left, TraceRecord right)
    {
        var leftJson = JsonSerializer.Serialize(TraceRecordMapper.ToDetails(left));
        var rightJson = JsonSerializer.Serialize(TraceRecordMapper.ToDetails(right));
        return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
    }

    private static ApplicationError InvalidTraceRecordId()
    {
        return ApplicationError.Validation(
            "Traceability.TraceRecordIdInvalid",
            "TraceRecordId cannot be an empty GUID.");
    }

    private static ApplicationError NotFound(Guid traceRecordId)
    {
        return ApplicationError.NotFound(
            "Traceability.RecordNotFound",
            $"Trace record {traceRecordId:D} was not found.");
    }
}
