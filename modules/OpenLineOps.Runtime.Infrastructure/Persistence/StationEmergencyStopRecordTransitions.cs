using OpenLineOps.Runtime.Application.Safety;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class StationEmergencyStopRecordTransitions
{
    public static StationEmergencyStopRecord Create(
        StationEmergencyStopRequestEvidence request) => new(
        request,
        StationEmergencyStopStatus.Pending,
        Acknowledgement: null,
        DispatchAttemptCount: 0,
        LastDispatchFailure: null,
        request.RequestedAtUtc,
        [
            new StationSafetyEvidence(
                1,
                StationSafetyEvidenceKind.EmergencyStopRequested,
                request.MessageId,
                request.RequestedAtUtc,
                FailureCode: null,
                FailureReason: null)
        ]);

    public static StationEmergencyStopRecord DispatchFailed(
        StationEmergencyStopRecord current,
        Guid requestMessageId,
        string failureCode,
        string failureReason,
        DateTimeOffset failedAtUtc)
    {
        if (current.Request.MessageId != requestMessageId
            || current.Status != StationEmergencyStopStatus.Pending)
        {
            throw new StationEmergencyStopIdempotencyConflictException(
                "Emergency Stop dispatch failure does not match one pending request.");
        }

        StationSafetyCanonical.RequireText(failureCode, nameof(failureCode));
        StationSafetyCanonical.RequireText(failureReason, nameof(failureReason), 2048);
        StationSafetyCanonical.RequireUtc(failedAtUtc, nameof(failedAtUtc));
        var sequence = checked(current.Evidence.Max(static evidence => evidence.Sequence) + 1);
        return current with
        {
            DispatchAttemptCount = checked(current.DispatchAttemptCount + 1),
            LastDispatchFailure = failureReason,
            LastUpdatedAtUtc = failedAtUtc,
            Evidence = current.Evidence.Append(new StationSafetyEvidence(
                sequence,
                StationSafetyEvidenceKind.EmergencyStopDispatchFailed,
                requestMessageId,
                failedAtUtc,
                failureCode,
                failureReason)).ToArray()
        };
    }

    public static StationEmergencyStopRecord Acknowledge(
        StationEmergencyStopRecord current,
        StationEmergencyStopAcknowledgementEvidence acknowledgement)
    {
        if (current.Acknowledgement is not null)
        {
            if (StationSafetyCanonical.SameAcknowledgement(
                    current.Acknowledgement,
                    acknowledgement))
            {
                return current;
            }

            throw new StationEmergencyStopIdempotencyConflictException(
                "Emergency Stop acknowledgement was reused with different evidence.");
        }

        if (current.Status != StationEmergencyStopStatus.Pending
            || acknowledgement.RequestMessageId != current.Request.MessageId
            || !string.Equals(
                acknowledgement.IdempotencyKey,
                current.Request.IdempotencyKey,
                StringComparison.Ordinal)
            || !string.Equals(
                acknowledgement.AgentId,
                current.Request.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                acknowledgement.StationId,
                current.Request.StationId,
                StringComparison.Ordinal)
            || acknowledgement.MessageId == Guid.Empty
            || acknowledgement.AcknowledgedAtUtc.Offset != TimeSpan.Zero
            || acknowledgement.AcknowledgedAtUtc < current.Request.RequestedAtUtc
            || (acknowledgement.Accepted
                && (acknowledgement.FailureCode is not null
                    || acknowledgement.FailureReason is not null))
            || (!acknowledgement.Accepted
                && (!StationSafetyCanonical.IsText(acknowledgement.FailureCode)
                    || !StationSafetyCanonical.IsText(acknowledgement.FailureReason))))
        {
            throw new StationEmergencyStopIdempotencyConflictException(
                "Emergency Stop acknowledgement does not match its persisted request evidence.");
        }

        var status = acknowledgement.Accepted
            ? StationEmergencyStopStatus.Acknowledged
            : StationEmergencyStopStatus.Rejected;
        var kind = acknowledgement.Accepted
            ? StationSafetyEvidenceKind.EmergencyStopAcknowledged
            : StationSafetyEvidenceKind.EmergencyStopRejected;
        var sequence = checked(current.Evidence.Max(static evidence => evidence.Sequence) + 1);
        return current with
        {
            Status = status,
            Acknowledgement = acknowledgement,
            DispatchAttemptCount = checked(current.DispatchAttemptCount + 1),
            LastDispatchFailure = null,
            LastUpdatedAtUtc = acknowledgement.AcknowledgedAtUtc,
            Evidence = current.Evidence.Append(new StationSafetyEvidence(
                sequence,
                kind,
                acknowledgement.MessageId,
                acknowledgement.AcknowledgedAtUtc,
                acknowledgement.FailureCode,
                acknowledgement.FailureReason)).ToArray()
        };
    }

    public static bool Matches(
        StationEmergencyStopRecord record,
        StationEmergencyStopQuery query) =>
        string.Equals(record.Request.ProjectId, query.ProjectId, StringComparison.Ordinal)
        && string.Equals(record.Request.ApplicationId, query.ApplicationId, StringComparison.Ordinal)
        && (query.ProjectSnapshotId is null || string.Equals(
            record.Request.ProjectSnapshotId,
            query.ProjectSnapshotId,
            StringComparison.Ordinal))
        && (query.StationSystemId is null || string.Equals(
            record.Request.StationSystemId,
            query.StationSystemId,
            StringComparison.Ordinal));
}
