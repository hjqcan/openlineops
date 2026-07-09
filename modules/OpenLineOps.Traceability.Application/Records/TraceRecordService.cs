using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Traceability.Application.Judgements;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Records;

public sealed class TraceRecordService : ITraceRecordService
{
    private const string PackageFormatVersion = "openlineops.trace-package.v1";

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

    public async Task<Result<TraceRecordDetails>> CreateCompletedAsync(
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

            var traceRecordId = request.TraceRecordId is null
                ? TraceRecordId.New()
                : new TraceRecordId(request.TraceRecordId.Value);
            var existing = await _repository
                .GetByIdAsync(traceRecordId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return Result.Failure<TraceRecordDetails>(ApplicationError.Conflict(
                    "Traceability.RecordAlreadyExists",
                    $"Trace record {traceRecordId} already exists."));
            }

            var traceRecord = TraceRecord.CreateCompleted(
                traceRecordId,
                new RuntimeSessionId(request.RuntimeSessionId),
                request.SerialNumber!,
                request.BatchId,
                new StationId(request.StationId!),
                request.FixtureId,
                new ProcessDefinitionId(request.ProcessDefinitionId!),
                new ProcessVersionId(request.ProcessVersionId!),
                new ConfigurationSnapshotId(request.ConfigurationSnapshotId!),
                new RecipeSnapshotId(request.RecipeSnapshotId!),
                new DeviceId(request.DeviceId!),
                judgement.Value,
                request.StartedAtUtc,
                request.CompletedAtUtc,
                new ActorId(request.RecordedBy!));

            foreach (var measurementRequest in request.Measurements ?? [])
            {
                var result = traceRecord.AddMeasurement(ToMeasurement(measurementRequest));
                if (!result.Succeeded)
                {
                    return Result.Failure<TraceRecordDetails>(
                        ApplicationError.Validation(result.Code, result.Message));
                }
            }

            foreach (var artifactRequest in request.Artifacts ?? [])
            {
                var result = traceRecord.AttachArtifact(ToArtifact(artifactRequest));
                if (!result.Succeeded)
                {
                    return Result.Failure<TraceRecordDetails>(
                        ApplicationError.Validation(result.Code, result.Message));
                }
            }

            foreach (var auditEntryRequest in request.AuditEntries ?? [])
            {
                var result = traceRecord.RecordAudit(ToAuditEntry(auditEntryRequest));
                if (!result.Succeeded)
                {
                    return Result.Failure<TraceRecordDetails>(
                        ApplicationError.Validation(result.Code, result.Message));
                }
            }

            await _repository
                .SaveAsync(traceRecord, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success(TraceRecordMapper.ToDetails(traceRecord));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Result.Failure<TraceRecordDetails>(ApplicationError.Validation(
                "Traceability.InvalidRecordInput",
                exception.Message));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<TraceRecordDetails>(ApplicationError.Validation(
                "Traceability.InvalidRecordInput",
                exception.Message));
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

        if (query.CompletedFromUtc is not null
            && query.CompletedToUtc is not null
            && query.CompletedToUtc < query.CompletedFromUtc)
        {
            return Result.Failure<PagedResult<TraceRecordSummary>>(ApplicationError.Validation(
                "Traceability.InvalidTimeRange",
                "CompletedToUtc cannot be earlier than CompletedFromUtc."));
        }

        var result = await _repository
            .QueryAsync(query, cancellationToken)
            .ConfigureAwait(false);
        var summaries = result.Items
            .Select(TraceRecordMapper.ToSummary)
            .ToArray();

        return Result.Success(new PagedResult<TraceRecordSummary>(
            summaries,
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

    private static MeasurementRecord ToMeasurement(CreateMeasurementRecordRequest request)
    {
        ValidateMeasurementRequest(request);

        return new MeasurementRecord(
            request.MeasurementRecordId is null
                ? MeasurementRecordId.New()
                : new MeasurementRecordId(request.MeasurementRecordId.Value),
            request.Name!,
            request.NumericValue,
            request.TextValue,
            request.Unit,
            new DeviceId(request.DeviceId!),
            request.RuntimeCommandId is null
                ? null
                : new RuntimeCommandId(request.RuntimeCommandId.Value),
            request.Passed,
            request.MeasuredAtUtc);
    }

    private static ArtifactRecord ToArtifact(CreateArtifactRecordRequest request)
    {
        ValidateArtifactRequest(request);

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
            new DeviceId(request.DeviceId!),
            request.CapturedAtUtc);
    }

    private static AuditEntry ToAuditEntry(CreateAuditEntryRequest request)
    {
        ValidateAuditEntryRequest(request);

        return new AuditEntry(
            request.AuditEntryId is null
                ? AuditEntryId.New()
                : new AuditEntryId(request.AuditEntryId.Value),
            new ActorId(request.ActorId!),
            request.Action!,
            request.Detail,
            request.OccurredAtUtc);
    }

    private static ApplicationError? ValidateCreateRequest(CreateTraceRecordRequest request)
    {
        if (request.TraceRecordId == Guid.Empty)
        {
            return ApplicationError.Validation(
                "Traceability.TraceRecordIdInvalid",
                "TraceRecordId cannot be an empty GUID.");
        }

        if (request.RuntimeSessionId == Guid.Empty)
        {
            return ApplicationError.Validation(
                "Traceability.RuntimeSessionIdRequired",
                "RuntimeSessionId is required.");
        }

        if (request.StartedAtUtc == default)
        {
            return ApplicationError.Validation(
                "Traceability.StartedAtRequired",
                "StartedAtUtc is required.");
        }

        if (request.CompletedAtUtc == default)
        {
            return ApplicationError.Validation(
                "Traceability.CompletedAtRequired",
                "CompletedAtUtc is required.");
        }

        if (request.CompletedAtUtc < request.StartedAtUtc)
        {
            return ApplicationError.Validation(
                "Traceability.CompletedBeforeStarted",
                "CompletedAtUtc cannot be earlier than StartedAtUtc.");
        }

        return RequiredError(request.SerialNumber, "SerialNumber")
            ?? RequiredError(request.StationId, "StationId")
            ?? RequiredError(request.ProcessDefinitionId, "ProcessDefinitionId")
            ?? RequiredError(request.ProcessVersionId, "ProcessVersionId")
            ?? RequiredError(request.ConfigurationSnapshotId, "ConfigurationSnapshotId")
            ?? RequiredError(request.RecipeSnapshotId, "RecipeSnapshotId")
            ?? RequiredError(request.DeviceId, "DeviceId")
            ?? RequiredError(request.RecordedBy, "RecordedBy");
    }

    private static void ValidateMeasurementRequest(CreateMeasurementRecordRequest request)
    {
        if (request.MeasurementRecordId == Guid.Empty)
        {
            throw new ArgumentException("MeasurementRecordId cannot be an empty GUID.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Measurement name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            throw new ArgumentException("Measurement device id is required.");
        }

        if (request.NumericValue is null && string.IsNullOrWhiteSpace(request.TextValue))
        {
            throw new ArgumentException("Measurement must include a numeric or text value.");
        }

        if (request.RuntimeCommandId == Guid.Empty)
        {
            throw new ArgumentException("RuntimeCommandId cannot be an empty GUID.");
        }

        if (request.MeasuredAtUtc == default)
        {
            throw new ArgumentException("MeasuredAtUtc is required.");
        }
    }

    private static void ValidateArtifactRequest(CreateArtifactRecordRequest request)
    {
        if (request.ArtifactRecordId == Guid.Empty)
        {
            throw new ArgumentException("ArtifactRecordId cannot be an empty GUID.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Artifact name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            throw new ArgumentException("Artifact kind is required.");
        }

        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            throw new ArgumentException("Artifact storage key is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            throw new ArgumentException("Artifact device id is required.");
        }

        if (request.CapturedAtUtc == default)
        {
            throw new ArgumentException("CapturedAtUtc is required.");
        }
    }

    private static void ValidateAuditEntryRequest(CreateAuditEntryRequest request)
    {
        if (request.AuditEntryId == Guid.Empty)
        {
            throw new ArgumentException("AuditEntryId cannot be an empty GUID.");
        }

        if (string.IsNullOrWhiteSpace(request.ActorId))
        {
            throw new ArgumentException("Audit actor id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Action))
        {
            throw new ArgumentException("Audit action is required.");
        }

        if (request.OccurredAtUtc == default)
        {
            throw new ArgumentException("OccurredAtUtc is required.");
        }
    }

    private static TEnum ParseEnum<TEnum>(string value, string errorCode)
        where TEnum : struct
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"{errorCode}: '{value}' is not supported.");
    }

    private static ApplicationError? RequiredError(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ApplicationError.Validation(
                $"Traceability.{fieldName}Required",
                $"{fieldName} is required.")
            : null;
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
