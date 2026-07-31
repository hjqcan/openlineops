using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Stations;

public enum StationControllerHandshakeFactKind
{
    Reported,
    ControllerSessionChanged,
    RecoveryRequired,
    RecoveryAcknowledged
}

public sealed record StationControllerHandshakeReport
{
    public StationControllerHandshakeReport(
        string controllerSessionId,
        long heartbeatSequence,
        long commandSequence,
        long acknowledgedCommandSequence,
        bool busy,
        bool completed,
        bool error,
        string? errorCode,
        bool recipeConfirmed,
        string? confirmedRecipeId,
        string? confirmedRecipeVersion,
        bool safetyPermitGranted,
        DateTimeOffset sourceTimestampUtc,
        DateTimeOffset receivedAtUtc,
        string ownerAgentId,
        string ownerAgentInstanceId,
        long agentFencingToken,
        string? commandId = null,
        long commandFencingToken = 0,
        StationMode observedMode = StationMode.Automatic,
        StationState observedState = StationState.Stopped,
        long stateSequence = 1)
    {
        ControllerSessionId = StationControllerHandshakeGuard.Canonical(
            controllerSessionId,
            nameof(controllerSessionId));
        ArgumentOutOfRangeException.ThrowIfLessThan(heartbeatSequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(commandSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(acknowledgedCommandSequence);
        if (acknowledgedCommandSequence > commandSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acknowledgedCommandSequence),
                acknowledgedCommandSequence,
                "Acknowledged command sequence cannot exceed the command sequence.");
        }

        if (commandSequence == 0)
        {
            if (commandId is not null || commandFencingToken != 0)
            {
                throw new ArgumentException(
                    "Controller command identity and fencing token must be empty "
                    + "before the first command.");
            }
        }
        else
        {
            CommandId = StationControllerHandshakeGuard.Canonical(
                commandId,
                nameof(commandId));
            ArgumentOutOfRangeException.ThrowIfLessThan(
                commandFencingToken,
                1);
        }

        if (!Enum.IsDefined(observedMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedMode),
                observedMode,
                "Observed controller mode is invalid.");
        }

        if (!Enum.IsDefined(observedState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedState),
                observedState,
                "Observed controller state is invalid.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(stateSequence, 1);

        var assertedStates = (busy ? 1 : 0) + (completed ? 1 : 0) + (error ? 1 : 0);
        if (assertedStates > 1)
        {
            throw new ArgumentException(
                "Busy, completed, and error controller states are mutually exclusive.");
        }

        if (error)
        {
            ErrorCode = StationControllerHandshakeGuard.Canonical(
                errorCode,
                nameof(errorCode));
        }
        else if (errorCode is not null)
        {
            throw new ArgumentException(
                "Error code must be null when the controller error flag is false.",
                nameof(errorCode));
        }

        if (recipeConfirmed)
        {
            ConfirmedRecipeId = StationControllerHandshakeGuard.Canonical(
                confirmedRecipeId,
                nameof(confirmedRecipeId));
            ConfirmedRecipeVersion = StationControllerHandshakeGuard.Canonical(
                confirmedRecipeVersion,
                nameof(confirmedRecipeVersion));
        }
        else if (confirmedRecipeId is not null || confirmedRecipeVersion is not null)
        {
            throw new ArgumentException(
                "Confirmed recipe identity and version must be null until recipe confirmation succeeds.");
        }

        StationControllerHandshakeGuard.Utc(sourceTimestampUtc, nameof(sourceTimestampUtc));
        StationControllerHandshakeGuard.Utc(receivedAtUtc, nameof(receivedAtUtc));
        OwnerAgentId = StationAgentControlLease.RequireOwnerIdentity(
            ownerAgentId,
            nameof(ownerAgentId));
        OwnerAgentInstanceId = StationAgentControlLease.RequireOwnerInstanceId(
            ownerAgentInstanceId,
            nameof(ownerAgentInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(agentFencingToken, 1);

        HeartbeatSequence = heartbeatSequence;
        CommandSequence = commandSequence;
        AcknowledgedCommandSequence = acknowledgedCommandSequence;
        CommandFencingToken = commandFencingToken;
        ObservedMode = observedMode;
        ObservedState = observedState;
        StateSequence = stateSequence;
        Busy = busy;
        Completed = completed;
        Error = error;
        RecipeConfirmed = recipeConfirmed;
        SafetyPermitGranted = safetyPermitGranted;
        SourceTimestampUtc = sourceTimestampUtc;
        ReceivedAtUtc = receivedAtUtc;
        AgentFencingToken = agentFencingToken;
    }

    public string ControllerSessionId { get; }

    public long HeartbeatSequence { get; }

    public long CommandSequence { get; }

    public long AcknowledgedCommandSequence { get; }

    public string OwnerAgentId { get; }

    public string OwnerAgentInstanceId { get; }

    public long AgentFencingToken { get; }

    public string? CommandId { get; }

    public long CommandFencingToken { get; }

    public StationMode ObservedMode { get; }

    public StationState ObservedState { get; }

    public long StateSequence { get; }

    public bool Busy { get; }

    public bool Completed { get; }

    public bool Error { get; }

    public string? ErrorCode { get; }

    public bool RecipeConfirmed { get; }

    public string? ConfirmedRecipeId { get; }

    public string? ConfirmedRecipeVersion { get; }

    public bool SafetyPermitGranted { get; }

    public DateTimeOffset SourceTimestampUtc { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public bool HasSamePayload(StationControllerHandshakeReport other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(
                ControllerSessionId,
                other.ControllerSessionId,
                StringComparison.Ordinal)
            && HeartbeatSequence == other.HeartbeatSequence
            && CommandSequence == other.CommandSequence
            && AcknowledgedCommandSequence == other.AcknowledgedCommandSequence
            && string.Equals(
                OwnerAgentId,
                other.OwnerAgentId,
                StringComparison.Ordinal)
            && string.Equals(
                OwnerAgentInstanceId,
                other.OwnerAgentInstanceId,
                StringComparison.Ordinal)
            && AgentFencingToken == other.AgentFencingToken
            && string.Equals(CommandId, other.CommandId, StringComparison.Ordinal)
            && CommandFencingToken == other.CommandFencingToken
            && ObservedMode == other.ObservedMode
            && ObservedState == other.ObservedState
            && StateSequence == other.StateSequence
            && Busy == other.Busy
            && Completed == other.Completed
            && Error == other.Error
            && string.Equals(ErrorCode, other.ErrorCode, StringComparison.Ordinal)
            && RecipeConfirmed == other.RecipeConfirmed
            && string.Equals(
                ConfirmedRecipeId,
                other.ConfirmedRecipeId,
                StringComparison.Ordinal)
            && string.Equals(
                ConfirmedRecipeVersion,
                other.ConfirmedRecipeVersion,
                StringComparison.Ordinal)
            && SafetyPermitGranted == other.SafetyPermitGranted
            && SourceTimestampUtc == other.SourceTimestampUtc;
    }

    public bool HasSameOperationalState(StationControllerHandshakeReport other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(
                ControllerSessionId,
                other.ControllerSessionId,
                StringComparison.Ordinal)
            && CommandSequence == other.CommandSequence
            && AcknowledgedCommandSequence == other.AcknowledgedCommandSequence
            && string.Equals(
                OwnerAgentId,
                other.OwnerAgentId,
                StringComparison.Ordinal)
            && string.Equals(
                OwnerAgentInstanceId,
                other.OwnerAgentInstanceId,
                StringComparison.Ordinal)
            && AgentFencingToken == other.AgentFencingToken
            && string.Equals(CommandId, other.CommandId, StringComparison.Ordinal)
            && CommandFencingToken == other.CommandFencingToken
            && ObservedMode == other.ObservedMode
            && ObservedState == other.ObservedState
            && StateSequence == other.StateSequence
            && Busy == other.Busy
            && Completed == other.Completed
            && Error == other.Error
            && string.Equals(ErrorCode, other.ErrorCode, StringComparison.Ordinal)
            && RecipeConfirmed == other.RecipeConfirmed
            && string.Equals(
                ConfirmedRecipeId,
                other.ConfirmedRecipeId,
                StringComparison.Ordinal)
            && string.Equals(
                ConfirmedRecipeVersion,
                other.ConfirmedRecipeVersion,
                StringComparison.Ordinal)
            && SafetyPermitGranted == other.SafetyPermitGranted;
    }
}

public sealed record StationControllerHandshakeSnapshot(
    StationId StationId,
    StationControllerHandshakeReport LatestReport,
    bool RecoveryRequired,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    IReadOnlyList<string>? RetiredControllerSessionIds = null,
    long RecoveryEpoch = 0,
    long OperationalEpoch = 1);

public sealed record StationControllerHandshakeFact
{
    public StationControllerHandshakeFact(
        long sequence,
        StationId stationId,
        StationControllerHandshakeFactKind kind,
        StationControllerHandshakeReport report,
        bool recoveryRequired,
        string actorId,
        string reason,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentNullException.ThrowIfNull(stationId);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Controller handshake fact kind is invalid.");
        }

        ArgumentNullException.ThrowIfNull(report);
        StationControllerHandshakeGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        if (occurredAtUtc < report.ReceivedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(occurredAtUtc),
                occurredAtUtc,
                "Controller handshake fact cannot precede its server-received report.");
        }

        Sequence = sequence;
        StationId = stationId;
        Kind = kind;
        Report = report;
        RecoveryRequired = recoveryRequired;
        ActorId = StationControllerHandshakeGuard.Canonical(
            actorId,
            nameof(actorId));
        Reason = StationControllerHandshakeGuard.Canonical(reason, nameof(reason));
        OccurredAtUtc = occurredAtUtc;
    }

    public long Sequence { get; }

    public StationId StationId { get; }

    public StationControllerHandshakeFactKind Kind { get; }

    public StationControllerHandshakeReport Report { get; }

    public bool RecoveryRequired { get; }

    public string ActorId { get; }

    public string Reason { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}

public sealed record StationControllerHandshakeMutation(
    bool Succeeded,
    bool Changed,
    string Code,
    string Message,
    StationControllerHandshakeFactKind? FactKind)
{
    public static StationControllerHandshakeMutation Accepted(
        bool changed,
        string message,
        StationControllerHandshakeFactKind? factKind = null) =>
        new(true, changed, string.Empty, message, factKind);

    public static StationControllerHandshakeMutation Rejected(
        string code,
        string message) =>
        new(false, false, code, message, null);
}

public sealed class StationControllerHandshake
{
    public const int MaximumRetiredControllerSessions = 64;
    private readonly HashSet<string> _retiredControllerSessionIds;

    private StationControllerHandshake(
        StationId stationId,
        StationControllerHandshakeReport latestReport,
        bool recoveryRequired,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastChangedAtUtc,
        IEnumerable<string>? retiredControllerSessionIds = null,
        long recoveryEpoch = 0,
        long operationalEpoch = 1)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(latestReport);
        StationControllerHandshakeGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        StationControllerHandshakeGuard.Utc(lastChangedAtUtc, nameof(lastChangedAtUtc));
        if (latestReport.ReceivedAtUtc > lastChangedAtUtc
            || lastChangedAtUtc < createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastChangedAtUtc),
                lastChangedAtUtc,
                "Handshake timestamps must form a valid server-observed timeline.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(recoveryEpoch);
        ArgumentOutOfRangeException.ThrowIfLessThan(operationalEpoch, 1);

        StationId = stationId;
        LatestReport = latestReport;
        RecoveryRequired = recoveryRequired;
        RecoveryEpoch = recoveryEpoch;
        OperationalEpoch = operationalEpoch;
        CreatedAtUtc = createdAtUtc;
        LastChangedAtUtc = lastChangedAtUtc;
        _retiredControllerSessionIds = (retiredControllerSessionIds ?? [])
            .Select(sessionId => StationControllerHandshakeGuard.Canonical(
                sessionId,
                nameof(retiredControllerSessionIds)))
            .ToHashSet(StringComparer.Ordinal);
        if (_retiredControllerSessionIds.Count
            > MaximumRetiredControllerSessions)
        {
            throw new ArgumentException(
                $"No more than {MaximumRetiredControllerSessions} retired "
                + "controller sessions may be restored.",
                nameof(retiredControllerSessionIds));
        }
        if (_retiredControllerSessionIds.Contains(
                latestReport.ControllerSessionId))
        {
            throw new ArgumentException(
                "The active controller session cannot also be retired.",
                nameof(retiredControllerSessionIds));
        }
    }

    public StationId StationId { get; }

    public StationControllerHandshakeReport LatestReport { get; private set; }

    public bool RecoveryRequired { get; private set; }

    public long RecoveryEpoch { get; private set; }

    public long OperationalEpoch { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastChangedAtUtc { get; private set; }

    public static StationControllerHandshake Create(
        StationId stationId,
        StationControllerHandshakeReport report)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(report);
        return new StationControllerHandshake(
            stationId,
            report,
            recoveryRequired: false,
            report.ReceivedAtUtc,
            report.ReceivedAtUtc,
            operationalEpoch: 1);
    }

    public static StationControllerHandshake Restore(
        StationControllerHandshakeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new StationControllerHandshake(
            snapshot.StationId,
            snapshot.LatestReport,
            snapshot.RecoveryRequired,
            snapshot.CreatedAtUtc,
            snapshot.LastChangedAtUtc,
            snapshot.RetiredControllerSessionIds,
            snapshot.RecoveryEpoch,
            snapshot.OperationalEpoch);
    }

    public StationControllerHandshakeMutation Report(
        StationControllerHandshakeReport report,
        bool allowCommandIdentityChangeForRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.ReceivedAtUtc < LastChangedAtUtc)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeServerTimeRegressed",
                "Controller handshake server-received time cannot move backwards.");
        }

        if (!string.Equals(
                report.ControllerSessionId,
                LatestReport.ControllerSessionId,
                StringComparison.Ordinal))
        {
            if (_retiredControllerSessionIds.Contains(report.ControllerSessionId))
            {
                return StationControllerHandshakeMutation.Rejected(
                    "Runtime.StationControllerHandshakeRetiredSession",
                    $"Controller session {report.ControllerSessionId} has been retired; "
                    + "a delayed report cannot reactivate it.");
            }

            if (_retiredControllerSessionIds.Count
                >= MaximumRetiredControllerSessions)
            {
                return StationControllerHandshakeMutation.Rejected(
                    "Runtime.StationControllerHandshakeSessionHistoryExhausted",
                    "Controller session history reached its safe retention "
                    + "limit; re-enroll the Station before accepting another "
                    + "controller session.");
            }

            _retiredControllerSessionIds.Add(LatestReport.ControllerSessionId);
            LatestReport = report;
            RecoveryRequired = true;
            RecoveryEpoch = checked(RecoveryEpoch + 1);
            OperationalEpoch = checked(OperationalEpoch + 1);
            LastChangedAtUtc = report.ReceivedAtUtc;
            return StationControllerHandshakeMutation.Accepted(
                changed: true,
                "Controller session changed; explicit recovery acknowledgement is required.",
                StationControllerHandshakeFactKind.ControllerSessionChanged);
        }

        if (report.HeartbeatSequence < LatestReport.HeartbeatSequence)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeOutOfOrder",
                $"Heartbeat sequence {report.HeartbeatSequence} is older than "
                + $"{LatestReport.HeartbeatSequence}.");
        }

        if (report.HeartbeatSequence == LatestReport.HeartbeatSequence)
        {
            return LatestReport.HasSamePayload(report)
                ? StationControllerHandshakeMutation.Accepted(
                    changed: false,
                    "Exact controller handshake replay accepted idempotently.")
                : StationControllerHandshakeMutation.Rejected(
                    "Runtime.StationControllerHandshakeIdempotencyConflict",
                    $"Heartbeat sequence {report.HeartbeatSequence} was already recorded "
                    + "with a different payload.");
        }

        if (report.CommandSequence < LatestReport.CommandSequence
            || report.AcknowledgedCommandSequence
                < LatestReport.AcknowledgedCommandSequence)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeCommandSequenceRegressed",
                "Controller command and acknowledgement sequences cannot move backwards "
                + "within one controller session.");
        }

        if (report.AgentFencingToken < LatestReport.AgentFencingToken)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeAgentFencingTokenRegressed",
                "Agent control fencing token cannot move backwards within one "
                + "controller session.");
        }

        if (report.AgentFencingToken == LatestReport.AgentFencingToken
            && (!string.Equals(
                    report.OwnerAgentId,
                    LatestReport.OwnerAgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    report.OwnerAgentInstanceId,
                    LatestReport.OwnerAgentInstanceId,
                    StringComparison.Ordinal)))
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeAgentOwnerChanged",
                "Agent control owner cannot change without advancing its "
                + "fencing token.");
        }

        if (report.CommandSequence == LatestReport.CommandSequence
            && (!string.Equals(
                    report.CommandId,
                    LatestReport.CommandId,
                    StringComparison.Ordinal)
                || report.CommandFencingToken
                    != LatestReport.CommandFencingToken))
        {
            if (!allowCommandIdentityChangeForRecovery)
            {
                return StationControllerHandshakeMutation.Rejected(
                    "Runtime.StationControllerHandshakeCommandIdentityChanged",
                    "Controller command identity and fencing token cannot change "
                    + "without advancing the command sequence.");
            }
        }

        if (report.CommandFencingToken < LatestReport.CommandFencingToken)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeFencingTokenRegressed",
                "Controller command fencing token cannot move backwards "
                + "within one controller session.");
        }

        if (report.StateSequence < LatestReport.StateSequence)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeStateSequenceRegressed",
                "Observed controller state sequence cannot move backwards.");
        }

        if (report.StateSequence == LatestReport.StateSequence
            && (report.ObservedMode != LatestReport.ObservedMode
                || report.ObservedState != LatestReport.ObservedState))
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeStateIdentityChanged",
                "Observed controller mode or state cannot change without "
                + "advancing the state sequence.");
        }

        if (report.SourceTimestampUtc < LatestReport.SourceTimestampUtc)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeSourceTimeRegressed",
                "Controller source time cannot move backwards within one controller session.");
        }

        var sameOperationalState =
            LatestReport.HasSameOperationalState(report);
        StationControllerHandshakeFactKind? factKind =
            sameOperationalState
            ? null
            : StationControllerHandshakeFactKind.Reported;
        LatestReport = report;
        if (!sameOperationalState)
        {
            OperationalEpoch = checked(OperationalEpoch + 1);
        }
        LastChangedAtUtc = report.ReceivedAtUtc;
        return StationControllerHandshakeMutation.Accepted(
            changed: true,
            "Controller handshake report accepted.",
            factKind);
    }

    public StationControllerHandshakeMutation AcknowledgeRecovery(
        string actorId,
        string reason,
        DateTimeOffset acknowledgedAtUtc)
    {
        _ = StationControllerHandshakeGuard.Canonical(actorId, nameof(actorId));
        _ = StationControllerHandshakeGuard.Canonical(reason, nameof(reason));
        StationControllerHandshakeGuard.Utc(acknowledgedAtUtc, nameof(acknowledgedAtUtc));
        if (acknowledgedAtUtc < LastChangedAtUtc)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeServerTimeRegressed",
                "Recovery acknowledgement time cannot precede the latest handshake.");
        }

        if (!RecoveryRequired)
        {
            return StationControllerHandshakeMutation.Accepted(
                changed: false,
                "Controller handshake recovery is already acknowledged.");
        }

        RecoveryRequired = false;
        OperationalEpoch = checked(OperationalEpoch + 1);
        LastChangedAtUtc = acknowledgedAtUtc;
        return StationControllerHandshakeMutation.Accepted(
            changed: true,
            "Controller handshake recovery acknowledged.",
            StationControllerHandshakeFactKind.RecoveryAcknowledged);
    }

    public StationControllerHandshakeMutation RequireRecovery(
        string actorId,
        string reason,
        DateTimeOffset requiredAtUtc)
    {
        _ = StationControllerHandshakeGuard.Canonical(actorId, nameof(actorId));
        _ = StationControllerHandshakeGuard.Canonical(reason, nameof(reason));
        StationControllerHandshakeGuard.Utc(requiredAtUtc, nameof(requiredAtUtc));
        if (requiredAtUtc < LastChangedAtUtc)
        {
            return StationControllerHandshakeMutation.Rejected(
                "Runtime.StationControllerHandshakeServerTimeRegressed",
                "Recovery-required time cannot precede the latest handshake state.");
        }

        if (RecoveryRequired)
        {
            return StationControllerHandshakeMutation.Accepted(
                changed: false,
                "Controller handshake recovery is already required.");
        }

        RecoveryRequired = true;
        RecoveryEpoch = checked(RecoveryEpoch + 1);
        OperationalEpoch = checked(OperationalEpoch + 1);
        LastChangedAtUtc = requiredAtUtc;
        return StationControllerHandshakeMutation.Accepted(
            changed: true,
            "Controller handshake recovery is now required.",
            StationControllerHandshakeFactKind.RecoveryRequired);
    }

    public StationControllerHandshakeSnapshot ToSnapshot() =>
        new(
            StationId,
            LatestReport,
            RecoveryRequired,
            CreatedAtUtc,
            LastChangedAtUtc,
            _retiredControllerSessionIds
                .Order(StringComparer.Ordinal)
                .ToArray(),
            RecoveryEpoch,
            OperationalEpoch);

    public StationControllerHandshakeFact CreateFact(
        long sequence,
        StationControllerHandshakeFactKind kind,
        string actorId,
        string reason,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        return new StationControllerHandshakeFact(
            sequence,
            StationId,
            kind,
            LatestReport,
            RecoveryRequired,
            StationControllerHandshakeGuard.Canonical(actorId, nameof(actorId)),
            StationControllerHandshakeGuard.Canonical(reason, nameof(reason)),
            occurredAtUtc);
    }
}

internal static class StationControllerHandshakeGuard
{
    private const int MaximumCanonicalTextLength = 512;

    public static string Canonical(string? value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumCanonicalTextLength
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Any(char.IsControl)
            ? throw new ArgumentException(
                $"{parameterName} must be canonical text no longer than "
                + $"{MaximumCanonicalTextLength} characters and contain no "
                + "control characters.",
                parameterName)
            : value;
    }

    public static void Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName);
        }
    }
}
