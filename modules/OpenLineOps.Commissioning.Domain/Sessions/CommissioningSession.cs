using OpenLineOps.Commissioning.Domain.Identifiers;
using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Commissioning.Domain.Sessions;

public sealed class CommissioningSession : AggregateRoot<CommissioningSessionId>
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(8);

    private readonly HashSet<CommissioningCapability> _capabilities;
    private readonly List<CommissioningAuditEntry> _auditTrail = [];

    private CommissioningSession(
        CommissioningSessionId id,
        string stationId,
        string requestedBy,
        string authorizedRole,
        string leaseId,
        long fencingToken,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        IEnumerable<CommissioningCapability> capabilities,
        CommissioningSessionStatus status = CommissioningSessionStatus.Active,
        string? recoveryReason = null,
        IEnumerable<CommissioningAuditEntry>? auditTrail = null)
        : base(id)
    {
        StationId = CommissioningGuard.Canonical(stationId, nameof(stationId));
        RequestedBy = CommissioningGuard.Canonical(requestedBy, nameof(requestedBy));
        AuthorizedRole = CommissioningGuard.Canonical(authorizedRole, nameof(authorizedRole));
        LeaseId = CommissioningGuard.Canonical(leaseId, nameof(leaseId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
        FencingToken = fencingToken;
        StartedAtUtc = CommissioningGuard.Utc(startedAtUtc, nameof(startedAtUtc));
        ExpiresAtUtc = CommissioningGuard.Utc(expiresAtUtc, nameof(expiresAtUtc));
        if (ExpiresAtUtc <= StartedAtUtc
            || ExpiresAtUtc - StartedAtUtc > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                $"Commissioning sessions must be positive and no longer than {MaximumDuration}.");
        }

        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities.ToHashSet();
        if (_capabilities.Count == 0 || _capabilities.Any(capability => !Enum.IsDefined(capability)))
        {
            throw new ArgumentException(
                "At least one defined commissioning capability is required.",
                nameof(capabilities));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        RecoveryReason = recoveryReason;
        if (auditTrail is null)
        {
            if (status != CommissioningSessionStatus.Active || recoveryReason is not null)
            {
                throw new ArgumentException(
                    "A new commissioning session must start active without recovery state.",
                    nameof(status));
            }

            Append(
                CommissioningAuditKind.SessionStarted,
                RequestedBy,
                StartedAtUtc,
                StationId,
                $"Authorized role: {AuthorizedRole}.");
        }
        else
        {
            RestoreAuditTrail(auditTrail);
            ValidateRestoredState();
        }
    }

    public string StationId { get; }

    public string RequestedBy { get; }

    public string AuthorizedRole { get; }

    public string LeaseId { get; }

    public long FencingToken { get; private set; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public CommissioningSessionStatus Status { get; private set; }

    public string? RecoveryReason { get; private set; }

    public IReadOnlySet<CommissioningCapability> Capabilities => _capabilities;

    public IReadOnlyList<CommissioningAuditEntry> AuditTrail => _auditTrail.AsReadOnly();

    public static CommissioningSession Start(
        CommissioningSessionId id,
        string stationId,
        string requestedBy,
        string authorizedRole,
        string leaseId,
        long fencingToken,
        DateTimeOffset startedAtUtc,
        DateTimeOffset expiresAtUtc,
        IEnumerable<CommissioningCapability> capabilities) =>
        new(
            id,
            stationId,
            requestedBy,
            authorizedRole,
            leaseId,
            fencingToken,
            startedAtUtc,
            expiresAtUtc,
            capabilities);

    public static CommissioningSession Restore(CommissioningSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new CommissioningSession(
            new CommissioningSessionId(snapshot.SessionId),
            snapshot.StationId,
            snapshot.RequestedBy,
            snapshot.AuthorizedRole,
            snapshot.LeaseId,
            snapshot.FencingToken,
            snapshot.StartedAtUtc,
            snapshot.ExpiresAtUtc,
            snapshot.Capabilities,
            snapshot.Status,
            snapshot.RecoveryReason,
            snapshot.AuditTrail);
    }

    public CommissioningSessionSnapshot ToSnapshot() =>
        new(
            Id.Value,
            StationId,
            RequestedBy,
            AuthorizedRole,
            LeaseId,
            FencingToken,
            StartedAtUtc,
            ExpiresAtUtc,
            Status,
            RecoveryReason,
            _capabilities.OrderBy(static capability => capability).ToArray(),
            _auditTrail.ToArray());

    public CommissioningOperationResult RenewLease(
        string actorId,
        long fencingToken,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset renewedAtUtc)
    {
        var active = RequireActiveOwner(actorId, renewedAtUtc, capability: null);
        if (!active.Succeeded)
        {
            return active;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
        CommissioningGuard.Utc(expiresAtUtc, nameof(expiresAtUtc));
        if (fencingToken <= FencingToken)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.StaleFencingToken",
                "A lease renewal must advance the fencing token.");
        }

        if (expiresAtUtc <= renewedAtUtc
            || expiresAtUtc - StartedAtUtc > MaximumDuration)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.InvalidExpiry",
                "A lease renewal cannot exceed the commissioning session duration boundary.");
        }

        FencingToken = fencingToken;
        ExpiresAtUtc = expiresAtUtc;
        Append(
            CommissioningAuditKind.LeaseRenewed,
            actorId,
            renewedAtUtc,
            LeaseId,
            "Exclusive station lease renewed.");
        return CommissioningOperationResult.Accepted("Commissioning lease renewed.");
    }

    public CommissioningOperationResult RecordDiagnosticAccess(
        string actorId,
        string deviceId,
        DateTimeOffset occurredAtUtc) =>
        AuthorizeAndAppend(
            actorId,
            CommissioningCapability.DeviceDiagnostics,
            CommissioningAuditKind.DiagnosticAccessed,
            deviceId,
            "Read-only device diagnostics accessed.",
            occurredAtUtc);

    public CommissioningOperationResult StartSignalMonitoring(
        string actorId,
        string subscriptionId,
        DateTimeOffset occurredAtUtc) =>
        AuthorizeAndAppend(
            actorId,
            CommissioningCapability.SignalMonitoring,
            CommissioningAuditKind.SignalMonitoringStarted,
            subscriptionId,
            "Read-only signal monitoring started.",
            occurredAtUtc);

    public CommissioningOperationResult AuthorizeManualCommand(
        string actorId,
        string commandId,
        StationMode stationMode,
        CommissioningActionSafetyClass safetyClass,
        bool debugActionWhitelisted,
        DateTimeOffset occurredAtUtc)
    {
        if (!Enum.IsDefined(stationMode) || !Enum.IsDefined(safetyClass))
        {
            throw new ArgumentOutOfRangeException(
                nameof(stationMode),
                "Station mode and safety class must be defined.");
        }

        var active = RequireActiveOwner(
            actorId,
            occurredAtUtc,
            CommissioningCapability.ManualCommand);
        if (!active.Succeeded)
        {
            return active;
        }

        if (safetyClass == CommissioningActionSafetyClass.SafetyCritical)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.SafetyActionForbidden",
                "Commissioning cannot authorize a safety-critical action.");
        }

        if (stationMode == StationMode.Automatic)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.AutomaticModeForbidden",
                "Manual commissioning commands are forbidden in Automatic mode.");
        }

        if (stationMode != StationMode.Simulation && !debugActionWhitelisted)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.ActionNotWhitelisted",
                "A physical commissioning command must be explicitly whitelisted.");
        }

        Append(
            CommissioningAuditKind.ManualCommandAuthorized,
            actorId,
            occurredAtUtc,
            CommissioningGuard.Canonical(commandId, nameof(commandId)),
            $"Mode={stationMode}; SafetyClass={safetyClass}; Whitelisted={debugActionWhitelisted}.");
        return CommissioningOperationResult.Accepted("Manual commissioning command authorized.");
    }

    public CommissioningOperationResult AuthorizeFlowStep(
        string actorId,
        string nodeId,
        DateTimeOffset occurredAtUtc) =>
        AuthorizeAndAppend(
            actorId,
            CommissioningCapability.FlowStep,
            CommissioningAuditKind.FlowStepAuthorized,
            nodeId,
            "Single Flow step authorized.",
            occurredAtUtc);

    public CommissioningOperationResult SetBreakpoint(
        string actorId,
        string nodeId,
        StationMode stationMode,
        bool debugActionWhitelisted,
        DateTimeOffset occurredAtUtc)
    {
        if (!Enum.IsDefined(stationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(stationMode));
        }

        var active = RequireActiveOwner(
            actorId,
            occurredAtUtc,
            CommissioningCapability.Breakpoint);
        if (!active.Succeeded)
        {
            return active;
        }

        if (stationMode != StationMode.Simulation && !debugActionWhitelisted)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.BreakpointForbidden",
                "Breakpoints require Simulation mode or a whitelisted debug action.");
        }

        Append(
            CommissioningAuditKind.BreakpointSet,
            actorId,
            occurredAtUtc,
            CommissioningGuard.Canonical(nodeId, nameof(nodeId)),
            $"Mode={stationMode}; Whitelisted={debugActionWhitelisted}.");
        return CommissioningOperationResult.Accepted("Breakpoint set.");
    }

    public CommissioningOperationResult RecoverInterruptedAction(
        string actionId,
        CommissioningActionIdempotencyClass idempotencyClass,
        DateTimeOffset occurredAtUtc)
    {
        CommissioningGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        if (!Enum.IsDefined(idempotencyClass))
        {
            throw new ArgumentOutOfRangeException(nameof(idempotencyClass));
        }

        if (Status == CommissioningSessionStatus.Active
            && occurredAtUtc >= ExpiresAtUtc)
        {
            Expire(occurredAtUtc);
            return InvalidStatus();
        }

        if (Status != CommissioningSessionStatus.Active)
        {
            return InvalidStatus();
        }

        if (idempotencyClass == CommissioningActionIdempotencyClass.Idempotent)
        {
            Append(
                CommissioningAuditKind.AutomaticReplayAuthorized,
                "openlineops.runtime",
                occurredAtUtc,
                CommissioningGuard.Canonical(actionId, nameof(actionId)),
                "Idempotent interrupted action may be replayed automatically.");
            return CommissioningOperationResult.Accepted(
                "Interrupted idempotent action may be replayed.");
        }

        Status = CommissioningSessionStatus.RecoveryRequired;
        RecoveryReason =
            $"Interrupted {idempotencyClass} action '{CommissioningGuard.Canonical(actionId, nameof(actionId))}' requires authorization.";
        Append(
            CommissioningAuditKind.RecoveryRequired,
            "openlineops.runtime",
            occurredAtUtc,
            actionId,
            RecoveryReason);
        return CommissioningOperationResult.Accepted("Commissioning recovery authorization required.");
    }

    public CommissioningOperationResult ResolveRecovery(
        string actorId,
        CommissioningRecoveryDisposition disposition,
        DateTimeOffset occurredAtUtc)
    {
        var actor = CommissioningGuard.Canonical(actorId, nameof(actorId));
        CommissioningGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        if (Status == CommissioningSessionStatus.RecoveryRequired
            && occurredAtUtc >= ExpiresAtUtc)
        {
            Expire(occurredAtUtc);
            return InvalidStatus();
        }

        if (Status != CommissioningSessionStatus.RecoveryRequired)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.RecoveryNotRequired",
                "The session is not waiting for recovery authorization.");
        }

        if (!string.Equals(actor, RequestedBy, StringComparison.Ordinal))
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.ActorNotAuthorized",
                "Only the authorized commissioning actor may resolve recovery.");
        }

        if (disposition == CommissioningRecoveryDisposition.Replay)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.NonIdempotentReplayForbidden",
                "An interrupted conditional or non-idempotent action cannot be replayed automatically.");
        }

        Status = disposition == CommissioningRecoveryDisposition.Abort
            ? CommissioningSessionStatus.Aborted
            : CommissioningSessionStatus.Active;
        Append(
            disposition == CommissioningRecoveryDisposition.Abort
                ? CommissioningAuditKind.SessionAborted
                : CommissioningAuditKind.RecoveryResolved,
            actor,
            occurredAtUtc,
            subjectId: null,
            $"Recovery disposition: {disposition}.");
        RecoveryReason = null;
        return CommissioningOperationResult.Accepted(
            disposition == CommissioningRecoveryDisposition.Abort
                ? "Commissioning session aborted."
                : "Commissioning recovery resolved.");
    }

    public CommissioningOperationResult Complete(
        string actorId,
        DateTimeOffset completedAtUtc)
    {
        var active = RequireActiveOwner(actorId, completedAtUtc, capability: null);
        if (!active.Succeeded)
        {
            return active;
        }

        Status = CommissioningSessionStatus.Completed;
        Append(
            CommissioningAuditKind.SessionCompleted,
            actorId,
            completedAtUtc,
            StationId,
            "Commissioning session completed.");
        return CommissioningOperationResult.Accepted("Commissioning session completed.");
    }

    public CommissioningOperationResult Expire(DateTimeOffset expiredAtUtc)
    {
        CommissioningGuard.Utc(expiredAtUtc, nameof(expiredAtUtc));
        if (Status is CommissioningSessionStatus.Completed
            or CommissioningSessionStatus.Aborted
            or CommissioningSessionStatus.Expired)
        {
            return InvalidStatus();
        }

        if (expiredAtUtc < ExpiresAtUtc)
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.LeaseNotExpired",
                "The exclusive commissioning lease has not reached its expiry boundary.");
        }

        Status = CommissioningSessionStatus.Expired;
        RecoveryReason = null;
        Append(
            CommissioningAuditKind.SessionExpired,
            "openlineops.runtime",
            expiredAtUtc,
            StationId,
            "Exclusive commissioning lease expired.");
        return CommissioningOperationResult.Accepted("Commissioning session expired.");
    }

    public CommissioningOperationResult Abort(
        string actorId,
        string reason,
        DateTimeOffset abortedAtUtc)
    {
        var actor = CommissioningGuard.Canonical(actorId, nameof(actorId));
        CommissioningGuard.Utc(abortedAtUtc, nameof(abortedAtUtc));
        if (Status is CommissioningSessionStatus.Active
                or CommissioningSessionStatus.RecoveryRequired
            && abortedAtUtc >= ExpiresAtUtc)
        {
            Expire(abortedAtUtc);
            return InvalidStatus();
        }

        if (Status is CommissioningSessionStatus.Completed
            or CommissioningSessionStatus.Aborted
            or CommissioningSessionStatus.Expired)
        {
            return InvalidStatus();
        }

        if (!string.Equals(actor, RequestedBy, StringComparison.Ordinal))
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.ActorNotAuthorized",
                "Only the authorized commissioning actor may abort this session.");
        }

        Status = CommissioningSessionStatus.Aborted;
        RecoveryReason = null;
        Append(
            CommissioningAuditKind.SessionAborted,
            actor,
            abortedAtUtc,
            StationId,
            CommissioningGuard.Canonical(reason, nameof(reason)));
        return CommissioningOperationResult.Accepted("Commissioning session aborted.");
    }

    private CommissioningOperationResult AuthorizeAndAppend(
        string actorId,
        CommissioningCapability capability,
        CommissioningAuditKind kind,
        string subjectId,
        string reason,
        DateTimeOffset occurredAtUtc)
    {
        var active = RequireActiveOwner(actorId, occurredAtUtc, capability);
        if (!active.Succeeded)
        {
            return active;
        }

        Append(
            kind,
            actorId,
            occurredAtUtc,
            CommissioningGuard.Canonical(subjectId, nameof(subjectId)),
            reason);
        return CommissioningOperationResult.Accepted(reason);
    }

    private CommissioningOperationResult RequireActiveOwner(
        string actorId,
        DateTimeOffset occurredAtUtc,
        CommissioningCapability? capability)
    {
        var actor = CommissioningGuard.Canonical(actorId, nameof(actorId));
        CommissioningGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        if (Status != CommissioningSessionStatus.Active)
        {
            return InvalidStatus();
        }

        if (occurredAtUtc >= ExpiresAtUtc)
        {
            Expire(occurredAtUtc);
            return InvalidStatus();
        }

        if (!string.Equals(actor, RequestedBy, StringComparison.Ordinal))
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.ActorNotAuthorized",
                "Only the authorized commissioning actor may use this session.");
        }

        if (capability is not null && !_capabilities.Contains(capability.Value))
        {
            return CommissioningOperationResult.Rejected(
                "Commissioning.PermissionDenied",
                $"The session does not grant {capability.Value}.");
        }

        return CommissioningOperationResult.Accepted("Commissioning session is active.");
    }

    private CommissioningOperationResult InvalidStatus() =>
        CommissioningOperationResult.Rejected(
            "Commissioning.SessionNotActive",
            $"Commissioning session {Id} is {Status}.");

    private void Append(
        CommissioningAuditKind kind,
        string actorId,
        DateTimeOffset occurredAtUtc,
        string? subjectId,
        string? reason)
    {
        _auditTrail.Add(new CommissioningAuditEntry(
            checked(_auditTrail.Count + 1L),
            kind,
            CommissioningGuard.Canonical(actorId, nameof(actorId)),
            CommissioningGuard.Utc(occurredAtUtc, nameof(occurredAtUtc)),
            subjectId,
            reason,
            FencingToken));
    }

    private void RestoreAuditTrail(IEnumerable<CommissioningAuditEntry> auditTrail)
    {
        ArgumentNullException.ThrowIfNull(auditTrail);
        foreach (var entry in auditTrail)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Sequence != checked(_auditTrail.Count + 1L)
                || !Enum.IsDefined(entry.Kind)
                || entry.FencingToken <= 0)
            {
                throw new InvalidDataException(
                    "Commissioning audit entries must have contiguous sequences, "
                    + "defined kinds, and positive fencing tokens.");
            }

            CommissioningGuard.Canonical(entry.ActorId, nameof(entry.ActorId));
            CommissioningGuard.Utc(entry.OccurredAtUtc, nameof(entry.OccurredAtUtc));
            if (entry.SubjectId is not null)
            {
                CommissioningGuard.Canonical(entry.SubjectId, nameof(entry.SubjectId));
            }

            if (entry.Reason is not null)
            {
                CommissioningGuard.Canonical(entry.Reason, nameof(entry.Reason));
            }

            if (entry.OccurredAtUtc < StartedAtUtc)
            {
                throw new InvalidDataException(
                    "Commissioning audit entries cannot precede the session.");
            }

            if (_auditTrail.Count > 0
                && (entry.OccurredAtUtc < _auditTrail[^1].OccurredAtUtc
                    || entry.FencingToken < _auditTrail[^1].FencingToken))
            {
                throw new InvalidDataException(
                    "Commissioning audit time and fencing tokens must be monotonic.");
            }

            _auditTrail.Add(entry);
        }
    }

    private void ValidateRestoredState()
    {
        if (_auditTrail.Count == 0
            || _auditTrail[0].Kind != CommissioningAuditKind.SessionStarted)
        {
            throw new InvalidDataException(
                "A persisted commissioning session must begin with SessionStarted evidence.");
        }

        var started = _auditTrail[0];
        if (!string.Equals(started.ActorId, RequestedBy, StringComparison.Ordinal)
            || !string.Equals(started.SubjectId, StationId, StringComparison.Ordinal)
            || started.OccurredAtUtc != StartedAtUtc)
        {
            throw new InvalidDataException(
                "SessionStarted commissioning evidence does not match the session identity.");
        }

        if (Status == CommissioningSessionStatus.RecoveryRequired
            && string.IsNullOrWhiteSpace(RecoveryReason))
        {
            throw new InvalidDataException(
                "RecoveryRequired commissioning sessions must retain a recovery reason.");
        }

        if (Status != CommissioningSessionStatus.RecoveryRequired
            && RecoveryReason is not null)
        {
            throw new InvalidDataException(
                "Only RecoveryRequired commissioning sessions may retain a recovery reason.");
        }

        if (RecoveryReason is not null)
        {
            CommissioningGuard.Canonical(RecoveryReason, nameof(RecoveryReason));
        }

        var lastKind = _auditTrail[^1].Kind;
        var stateMatchesLastFact = Status switch
        {
            CommissioningSessionStatus.Active =>
                lastKind is not (
                    CommissioningAuditKind.RecoveryRequired
                    or CommissioningAuditKind.SessionCompleted
                    or CommissioningAuditKind.SessionAborted
                    or CommissioningAuditKind.SessionExpired),
            CommissioningSessionStatus.RecoveryRequired =>
                lastKind == CommissioningAuditKind.RecoveryRequired,
            CommissioningSessionStatus.Completed =>
                lastKind == CommissioningAuditKind.SessionCompleted,
            CommissioningSessionStatus.Aborted =>
                lastKind == CommissioningAuditKind.SessionAborted,
            CommissioningSessionStatus.Expired =>
                lastKind == CommissioningAuditKind.SessionExpired,
            _ => false
        };
        if (!stateMatchesLastFact)
        {
            throw new InvalidDataException(
                "Commissioning session status does not match its latest audit fact.");
        }

        if (_auditTrail[^1].FencingToken != FencingToken)
        {
            throw new InvalidDataException(
                "The current fencing token must match the latest commissioning audit fact.");
        }
    }
}
