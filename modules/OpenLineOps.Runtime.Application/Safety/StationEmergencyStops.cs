using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Runtime.Application.Safety;

public enum StationEmergencyStopStatus
{
    Pending = 0,
    Acknowledged = 1,
    Rejected = 2
}

public enum StationSafetyEvidenceKind
{
    EmergencyStopRequested = 0,
    EmergencyStopDispatchFailed = 1,
    EmergencyStopAcknowledged = 2,
    EmergencyStopRejected = 3
}

public sealed record RequestStationEmergencyStop(
    Guid MessageId,
    string IdempotencyKey,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string StationSystemId,
    string ActorId,
    string Reason,
    DateTimeOffset RequestedAtUtc);

public sealed record StationEmergencyStopRequestEvidence(
    Guid MessageId,
    string IdempotencyKey,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string StationSystemId,
    string AgentId,
    string StationId,
    IReadOnlyCollection<Guid> RelatedProductionRunIds,
    string ActorId,
    string Reason,
    DateTimeOffset RequestedAtUtc);

public sealed record StationEmergencyStopAcknowledgementEvidence(
    Guid MessageId,
    Guid RequestMessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    bool Accepted,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset AcknowledgedAtUtc);

public sealed record StationSafetyEvidence(
    long Sequence,
    StationSafetyEvidenceKind Kind,
    Guid MessageId,
    DateTimeOffset OccurredAtUtc,
    string? FailureCode,
    string? FailureReason);

public sealed record StationEmergencyStopRecord(
    StationEmergencyStopRequestEvidence Request,
    StationEmergencyStopStatus Status,
    StationEmergencyStopAcknowledgementEvidence? Acknowledgement,
    int DispatchAttemptCount,
    string? LastDispatchFailure,
    DateTimeOffset LastUpdatedAtUtc,
    IReadOnlyCollection<StationSafetyEvidence> Evidence);

public sealed record StationEmergencyStopSubmission(
    StationEmergencyStopRecord Record,
    bool Replayed,
    bool DispatchAttempted);

public sealed record StationEmergencyStopQuery(
    string ProjectId,
    string ApplicationId,
    string? ProjectSnapshotId,
    string? StationSystemId);

public enum StationEmergencyStopRegistrationKind
{
    Created = 0,
    Replay = 1
}

public sealed record StationEmergencyStopRegistration(
    StationEmergencyStopRegistrationKind Kind,
    StationEmergencyStopRecord Record);

public interface IStationEmergencyStopRepository
{
    ValueTask<StationEmergencyStopRegistration> RegisterRequestAsync(
        StationEmergencyStopRequestEvidence request,
        CancellationToken cancellationToken = default);

    ValueTask<StationEmergencyStopRecord> RecordDispatchFailureAsync(
        string idempotencyKey,
        Guid requestMessageId,
        string failureCode,
        string failureReason,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<StationEmergencyStopRecord> RecordAcknowledgementAsync(
        StationEmergencyStopAcknowledgementEvidence acknowledgement,
        CancellationToken cancellationToken = default);

    ValueTask<StationEmergencyStopRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<StationEmergencyStopRecord>> ListAsync(
        StationEmergencyStopQuery query,
        CancellationToken cancellationToken = default);
}

public interface IStationEmergencyStopOperatorAuthorizer
{
    ValueTask AuthorizeAsync(
        StationEmergencyStopAuthorization authorization,
        CancellationToken cancellationToken = default);
}

public interface IStationEmergencyStopProductionRunLinker
{
    ValueTask<IReadOnlyCollection<Guid>> ListActiveProductionRunIdsAsync(
        StationEmergencyStopAuthorization scope,
        CancellationToken cancellationToken = default);
}

public sealed class StationEmergencyStopProductionRunLinker(
    IProductionRunRepository repository) : IStationEmergencyStopProductionRunLinker
{
    public async ValueTask<IReadOnlyCollection<Guid>> ListActiveProductionRunIdsAsync(
        StationEmergencyStopAuthorization scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var active = await repository.ListActiveAsync(
                productionLineDefinitionId: null,
                stationSystemId: scope.StationSystemId,
                slotId: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return active
            .Where(entry => string.Equals(
                    entry.Run.ProjectId,
                    scope.ProjectId,
                    StringComparison.Ordinal)
                && string.Equals(
                    entry.Run.ApplicationId,
                    scope.ApplicationId,
                    StringComparison.Ordinal)
                && string.Equals(
                    entry.Run.ProjectSnapshotId,
                    scope.ProjectSnapshotId,
                    StringComparison.Ordinal))
            .Select(entry => entry.Run.Id.Value)
            .OrderBy(static id => id)
            .ToArray();
    }
}

public sealed record StationEmergencyStopAuthorization(
    string ActorId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string StationSystemId);

public sealed class ExplicitStationEmergencyStopOperatorAuthorizer :
    IStationEmergencyStopOperatorAuthorizer
{
    public ValueTask AuthorizeAsync(
        StationEmergencyStopAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        cancellationToken.ThrowIfCancellationRequested();
        StationSafetyCanonical.RequireText(authorization.ActorId, nameof(authorization.ActorId));
        return ValueTask.CompletedTask;
    }
}

public sealed class StationEmergencyStopIdempotencyConflictException(string message) :
    InvalidOperationException(message);

public sealed class StationEmergencyStopDeploymentException(string message, Exception innerException) :
    InvalidOperationException(message, innerException);

public sealed class StationEmergencyStopService(
    IStationEmergencyStopRepository repository,
    IStationDeploymentResolver deploymentResolver,
    IStationEmergencyStopGateway gateway,
    IStationEmergencyStopOperatorAuthorizer operatorAuthorizer,
    IStationEmergencyStopProductionRunLinker productionRunLinker,
    IClock clock)
{
    public async ValueTask<StationEmergencyStopSubmission> RequestAsync(
        RequestStationEmergencyStop request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var acceptedAtUtc = UtcNow();
        if (request.RequestedAtUtc > acceptedAtUtc)
        {
            throw new ArgumentException(
                "Emergency Stop requestedAtUtc cannot be in the future.",
                nameof(request));
        }
        var authorization = new StationEmergencyStopAuthorization(
            request.ActorId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.StationSystemId);
        await operatorAuthorizer.AuthorizeAsync(
                authorization,
                cancellationToken)
            .ConfigureAwait(false);

        var existingRequest = await repository.GetByIdempotencyKeyAsync(
                request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingRequest is not null)
        {
            if (!StationSafetyCanonical.MatchesSubmission(existingRequest.Request, request))
            {
                throw new StationEmergencyStopIdempotencyConflictException(
                    $"Emergency Stop idempotency key '{request.IdempotencyKey}' was reused with different immutable evidence.");
            }

            if (existingRequest.Status != StationEmergencyStopStatus.Pending)
            {
                return new StationEmergencyStopSubmission(
                    existingRequest,
                    Replayed: true,
                    DispatchAttempted: false);
            }
        }

        StationDeploymentRoute route;
        try
        {
            route = await deploymentResolver.ResolveAsync(
                    new StationDeploymentRequest(
                        request.ProjectId,
                        request.ApplicationId,
                        request.ProjectSnapshotId,
                        request.StationSystemId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or InvalidDataException
                                           or DirectoryNotFoundException)
        {
            throw new StationEmergencyStopDeploymentException(
                $"No verified Station deployment resolves '{request.ProjectId}/{request.ApplicationId}/{request.ProjectSnapshotId}/{request.StationSystemId}'.",
                exception);
        }

        var relatedProductionRunIds = existingRequest?.Request.RelatedProductionRunIds
            ?? await productionRunLinker
                .ListActiveProductionRunIdsAsync(authorization, cancellationToken)
                .ConfigureAwait(false);
        if (relatedProductionRunIds.Any(static id => id == Guid.Empty)
            || relatedProductionRunIds.Distinct().Count() != relatedProductionRunIds.Count)
        {
            throw new InvalidDataException(
                "Emergency Stop Production Run links must be unique non-empty identities.");
        }

        var evidence = new StationEmergencyStopRequestEvidence(
            request.MessageId,
            request.IdempotencyKey,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.StationSystemId,
            StationSafetyCanonical.RequireText(route.AgentId, nameof(route.AgentId)),
            StationSafetyCanonical.RequireText(route.StationId, nameof(route.StationId)),
            relatedProductionRunIds.OrderBy(static id => id).ToArray(),
            request.ActorId,
            request.Reason,
            request.RequestedAtUtc);
        var registration = await repository.RegisterRequestAsync(evidence, cancellationToken)
            .ConfigureAwait(false);
        if (registration.Record.Status != StationEmergencyStopStatus.Pending)
        {
            return new StationEmergencyStopSubmission(
                registration.Record,
                Replayed: true,
                DispatchAttempted: false);
        }

        var message = new EmergencyStopRequested(
            evidence.MessageId,
            evidence.IdempotencyKey,
            evidence.AgentId,
            evidence.StationId,
            evidence.Reason,
            evidence.ActorId,
            evidence.RequestedAtUtc);
        EmergencyStopAcknowledged acknowledgement;
        try
        {
            acknowledgement = await gateway.RequestEmergencyStopAsync(message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var pending = await repository.RecordDispatchFailureAsync(
                    evidence.IdempotencyKey,
                    evidence.MessageId,
                    "Runtime.EmergencyStopTransportUnavailable",
                    CanonicalFailure(exception.Message),
                    UtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new StationEmergencyStopSubmission(
                pending,
                registration.Kind == StationEmergencyStopRegistrationKind.Replay,
                DispatchAttempted: true);
        }

        if (!Acknowledges(evidence, acknowledgement))
        {
            var pending = await repository.RecordDispatchFailureAsync(
                    evidence.IdempotencyKey,
                    evidence.MessageId,
                    "Runtime.EmergencyStopAcknowledgementMismatch",
                    "Station Agent acknowledgement identity does not match the persisted Emergency Stop request.",
                    UtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new StationEmergencyStopSubmission(
                pending,
                registration.Kind == StationEmergencyStopRegistrationKind.Replay,
                DispatchAttempted: true);
        }

        var acknowledged = await repository.RecordAcknowledgementAsync(
                new StationEmergencyStopAcknowledgementEvidence(
                    acknowledgement.MessageId,
                    acknowledgement.RequestMessageId,
                    acknowledgement.IdempotencyKey,
                    acknowledgement.AgentId,
                    acknowledgement.StationId,
                    acknowledgement.Accepted,
                    acknowledgement.FailureCode,
                    acknowledgement.FailureReason,
                    acknowledgement.AcknowledgedAtUtc),
                CancellationToken.None)
            .ConfigureAwait(false);
        return new StationEmergencyStopSubmission(
            acknowledged,
            registration.Kind == StationEmergencyStopRegistrationKind.Replay,
            DispatchAttempted: true);
    }

    public ValueTask<IReadOnlyCollection<StationEmergencyStopRecord>> ListAsync(
        StationEmergencyStopQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        StationSafetyCanonical.RequireText(query.ProjectId, nameof(query.ProjectId));
        StationSafetyCanonical.RequireText(query.ApplicationId, nameof(query.ApplicationId));
        StationSafetyCanonical.RequireOptionalText(query.ProjectSnapshotId, nameof(query.ProjectSnapshotId));
        StationSafetyCanonical.RequireOptionalText(query.StationSystemId, nameof(query.StationSystemId));
        return repository.ListAsync(query, cancellationToken);
    }

    private static void ValidateRequest(RequestStationEmergencyStop request)
    {
        if (request.MessageId == Guid.Empty)
        {
            throw new ArgumentException("Emergency Stop Message ID cannot be empty.", nameof(request));
        }

        StationSafetyCanonical.RequireLowercaseUuid(request.IdempotencyKey, nameof(request.IdempotencyKey));
        StationSafetyCanonical.RequireText(request.ProjectId, nameof(request.ProjectId));
        StationSafetyCanonical.RequireText(request.ApplicationId, nameof(request.ApplicationId));
        StationSafetyCanonical.RequireText(request.ProjectSnapshotId, nameof(request.ProjectSnapshotId));
        StationSafetyCanonical.RequireText(request.StationSystemId, nameof(request.StationSystemId));
        StationSafetyCanonical.RequireText(request.ActorId, nameof(request.ActorId));
        StationSafetyCanonical.RequireText(request.Reason, nameof(request.Reason), maximumLength: 2048);
        StationSafetyCanonical.RequireUtc(request.RequestedAtUtc, nameof(request.RequestedAtUtc));
    }

    private static bool Acknowledges(
        StationEmergencyStopRequestEvidence request,
        EmergencyStopAcknowledged acknowledgement) =>
        acknowledgement.MessageId != Guid.Empty
        && acknowledgement.RequestMessageId == request.MessageId
        && string.Equals(acknowledgement.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(acknowledgement.AgentId, request.AgentId, StringComparison.Ordinal)
        && string.Equals(acknowledgement.StationId, request.StationId, StringComparison.Ordinal)
        && acknowledgement.AcknowledgedAtUtc.Offset == TimeSpan.Zero
        && acknowledgement.AcknowledgedAtUtc != default
        && acknowledgement.AcknowledgedAtUtc >= request.RequestedAtUtc
        && ((acknowledgement.Accepted
             && acknowledgement.FailureCode is null
             && acknowledgement.FailureReason is null)
            || (!acknowledgement.Accepted
                && StationSafetyCanonical.IsText(acknowledgement.FailureCode)
                && StationSafetyCanonical.IsText(acknowledgement.FailureReason)));

    private DateTimeOffset UtcNow()
    {
        var value = clock.UtcNow;
        StationSafetyCanonical.RequireUtc(value, nameof(clock.UtcNow));
        return value;
    }

    private static string CanonicalFailure(string? value)
    {
        var canonical = string.IsNullOrWhiteSpace(value)
            ? "Station safety transport failed without an error description."
            : value.Trim();
        return canonical.Length <= 2048 ? canonical : canonical[..2048];
    }
}

public static class StationSafetyCanonical
{
    public static string RequireText(string? value, string name, int maximumLength = 512)
    {
        if (!IsText(value) || value!.Length > maximumLength)
        {
            throw new ArgumentException(
                $"{name} must be canonical non-empty text no longer than {maximumLength} characters.",
                name);
        }

        return value;
    }

    public static void RequireOptionalText(string? value, string name)
    {
        if (value is not null)
        {
            RequireText(value, name);
        }
    }

    public static bool IsText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1]);

    public static Guid RequireLowercaseUuid(string value, string name)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} must be one non-empty lowercase D-format UUID.", name);
        }

        return parsed;
    }

    public static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must be UTC.", name);
        }
    }

    public static bool SameRequest(
        StationEmergencyStopRequestEvidence left,
        StationEmergencyStopRequestEvidence right) =>
        left.MessageId == right.MessageId
        && string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
        && string.Equals(left.ApplicationId, right.ApplicationId, StringComparison.Ordinal)
        && string.Equals(left.ProjectSnapshotId, right.ProjectSnapshotId, StringComparison.Ordinal)
        && string.Equals(left.StationSystemId, right.StationSystemId, StringComparison.Ordinal)
        && string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal)
        && string.Equals(left.StationId, right.StationId, StringComparison.Ordinal)
        && left.RelatedProductionRunIds.SequenceEqual(right.RelatedProductionRunIds)
        && string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal)
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && left.RequestedAtUtc == right.RequestedAtUtc;

    public static bool MatchesSubmission(
        StationEmergencyStopRequestEvidence persisted,
        RequestStationEmergencyStop submitted) =>
        persisted.MessageId == submitted.MessageId
        && string.Equals(
            persisted.IdempotencyKey,
            submitted.IdempotencyKey,
            StringComparison.Ordinal)
        && string.Equals(persisted.ProjectId, submitted.ProjectId, StringComparison.Ordinal)
        && string.Equals(
            persisted.ApplicationId,
            submitted.ApplicationId,
            StringComparison.Ordinal)
        && string.Equals(
            persisted.ProjectSnapshotId,
            submitted.ProjectSnapshotId,
            StringComparison.Ordinal)
        && string.Equals(
            persisted.StationSystemId,
            submitted.StationSystemId,
            StringComparison.Ordinal)
        && string.Equals(persisted.ActorId, submitted.ActorId, StringComparison.Ordinal)
        && string.Equals(persisted.Reason, submitted.Reason, StringComparison.Ordinal)
        && persisted.RequestedAtUtc == submitted.RequestedAtUtc;

    public static bool SameAcknowledgement(
        StationEmergencyStopAcknowledgementEvidence left,
        StationEmergencyStopAcknowledgementEvidence right) =>
        left.MessageId == right.MessageId
        && left.RequestMessageId == right.RequestMessageId
        && string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal)
        && string.Equals(left.AgentId, right.AgentId, StringComparison.Ordinal)
        && string.Equals(left.StationId, right.StationId, StringComparison.Ordinal)
        && left.Accepted == right.Accepted
        && string.Equals(left.FailureCode, right.FailureCode, StringComparison.Ordinal)
        && string.Equals(left.FailureReason, right.FailureReason, StringComparison.Ordinal)
        && left.AcknowledgedAtUtc == right.AcknowledgedAtUtc;
}
