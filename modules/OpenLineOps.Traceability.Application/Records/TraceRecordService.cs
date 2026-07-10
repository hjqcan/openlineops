using System.Text.Json;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Traceability.Application.Judgements;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Records;

public sealed class TraceRecordService : ITraceRecordService
{
    private const string PackageFormatVersion = "openlineops.production-run-trace-package.v1";

    private readonly ITraceRecordRepository _repository;
    private readonly IClock _clock;
    private readonly ITraceJudgementGenerator _judgementGenerator;

    public TraceRecordService(
        ITraceRecordRepository repository,
        IClock clock,
        ITraceJudgementGenerator judgementGenerator)
    {
        _repository = repository;
        _clock = clock;
        _judgementGenerator = judgementGenerator;
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
            var judgement = _judgementGenerator.Generate(request);
            if (judgement.IsFailure)
            {
                return Result.Failure<TraceRecordDetails>(judgement.Error);
            }

            var runId = new ProductionRunId(request.ProductionRunId);
            var traceRecord = TraceRecord.Create(
                new TraceRecordId(runId.Value),
                runId,
                request.ProjectId!,
                request.ApplicationId!,
                request.ProjectSnapshotId!,
                request.TopologyId!,
                request.ProductionLineDefinitionId!,
                request.DutModelId!,
                request.DutIdentityInputKey!,
                request.DutIdentityValue!,
                request.BatchId,
                request.FixtureId,
                request.DeviceId,
                new ActorId(request.ActorId!),
                ParseEnum<TraceProductionRunStatus>(request.RunStatus!, "Traceability.InvalidRunStatus"),
                judgement.Value,
                request.CreatedAtUtc,
                request.StartedAtUtc,
                request.CompletedAtUtc,
                request.FailureCode,
                request.FailureReason,
                request.Stages!.Select(ToStage),
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
                PackageFormatVersion,
                _clock.UtcNow,
                details.Value));
    }

    private static TraceStageExecution ToStage(CreateTraceStageExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new TraceStageExecution(
            request.StageId!,
            request.Sequence,
            request.WorkstationId!,
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
            ParseEnum<TraceStageStatus>(request.Status!, "Traceability.InvalidStageStatus"),
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
            (request.Incidents ?? []).Select(ToIncident));
    }

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
            ParseEnum<TraceCommandStatus>(request.Status!, "Traceability.InvalidCommandStatus"),
            ParseOptionalEnum<TraceCommandSemanticOutcome>(
                request.SemanticOutcome,
                "Traceability.InvalidCommandSemanticOutcome"),
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
            ParseEnum<TraceCommandStatus>(request.CommandStatus!, "Traceability.InvalidCommandStatus"),
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
            ?? RequiredCanonical(request.DutModelId, "DutModelId")
            ?? RequiredCanonical(request.DutIdentityInputKey, "DutIdentityInputKey")
            ?? RequiredCanonical(request.DutIdentityValue, "DutIdentityValue")
            ?? RequiredCanonical(request.ActorId, "ActorId")
            ?? RequiredCanonical(request.RunStatus, "RunStatus")
            ?? OptionalCanonical(request.BatchId, "BatchId")
            ?? OptionalCanonical(request.FixtureId, "FixtureId")
            ?? OptionalCanonical(request.DeviceId, "DeviceId")
            ?? OptionalCanonical(request.FailureCode, "FailureCode")
            ?? OptionalCanonical(request.FailureReason, "FailureReason");
        if (requiredError is not null)
        {
            return requiredError;
        }

        if (request.Stages is null || request.Stages.Count == 0)
        {
            return ApplicationError.Validation(
                "Traceability.StagesRequired",
                "A production run trace must contain at least one stage.");
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
