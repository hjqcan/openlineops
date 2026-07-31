using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Stations;

public sealed record StationControllerHandshakeInput(
    string OwnerAgentInstanceId,
    long AgentFencingToken,
    string LeaseHandle,
    string ControllerSessionId,
    long HeartbeatSequence,
    long CommandSequence,
    long AcknowledgedCommandSequence,
    bool Busy,
    bool Completed,
    bool Error,
    string? ErrorCode,
    bool RecipeConfirmed,
    string? ConfirmedRecipeId,
    string? ConfirmedRecipeVersion,
    bool SafetyPermitGranted,
    DateTimeOffset SourceTimestampUtc,
    string? CommandId = null,
    long CommandFencingToken = 0,
    StationMode ObservedMode = StationMode.Automatic,
    StationState ObservedState = StationState.Stopped,
    long StateSequence = 1);

public sealed class StationControllerHandshakeService(
    IStationControllerHandshakeRepository repository,
    IStationLifecycleRepository lifecycleRepository,
    IStationAgentControlLeaseRepository agentControlLeaseRepository,
    IStationAgentControlLeaseValidator agentControlLeaseValidator,
    IClock clock,
    StationControllerHandshakeOptions options)
{
    public const int MaximumConcurrencyAttempts = 8;

    public async ValueTask<Result<StationControllerHandshakePersistenceEntry>> GetAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        var entry = await repository.GetByIdAsync(stationId, cancellationToken)
            .ConfigureAwait(false);
        return entry is null
            ? NotFound(stationId)
            : Result.Success(entry);
    }

    public async ValueTask<
        Result<IReadOnlyList<StationControllerHandshakeFact>>> ListFactsAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        if (await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Result.Failure<IReadOnlyList<StationControllerHandshakeFact>>(
                NotFoundError(stationId));
        }

        return Result.Success(
            await repository.ListFactsAsync(stationId, cancellationToken)
                .ConfigureAwait(false));
    }

    public async ValueTask<Result<StationControllerHandshakePersistenceEntry>> ReportAsync(
        StationId stationId,
        StationControllerHandshakeInput input,
        string actorId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentNullException.ThrowIfNull(input);
        _ = Required(actorId, nameof(actorId));
        _ = Required(reason, nameof(reason));
        _ = StationAgentControlLease.RequireOwnerInstanceId(
            input.OwnerAgentInstanceId,
            nameof(input.OwnerAgentInstanceId));
        ArgumentOutOfRangeException.ThrowIfLessThan(
            input.AgentFencingToken,
            1);
        if (await lifecycleRepository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Result.Failure<StationControllerHandshakePersistenceEntry>(
                ApplicationError.NotFound(
                    "Runtime.StationLifecycleNotFound",
                    $"Station lifecycle {stationId} must be enrolled before "
                    + "controller handshakes are accepted."));
        }

        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var leaseValidation = await agentControlLeaseValidator.ValidateAsync(
                    stationId,
                    actorId,
                    input.OwnerAgentInstanceId,
                    input.AgentFencingToken,
                    input.LeaseHandle,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!leaseValidation.IsValid)
            {
                return Result.Failure<
                    StationControllerHandshakePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationAgentControlLeaseInvalid",
                        "The reporting Agent process does not own the current "
                        + "Station control lease."));
            }

            var receivedAtUtc = clock.UtcNow;
            var report = ToReport(input, actorId, receivedAtUtc);
            if ((receivedAtUtc - report.SourceTimestampUtc).Duration()
                > options.MaximumSourceClockSkew)
            {
                return Result.Failure<StationControllerHandshakePersistenceEntry>(
                    ApplicationError.Validation(
                        "Runtime.StationControllerHandshakeClockSkew",
                        "Controller source time exceeds the configured clock-skew "
                        + "boundary and was not persisted."));
            }

            var lifecycle = await lifecycleRepository.GetByIdAsync(
                    stationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lifecycle is null)
            {
                return Result.Failure<StationControllerHandshakePersistenceEntry>(
                    ApplicationError.NotFound(
                        "Runtime.StationLifecycleNotFound",
                        $"Station lifecycle {stationId} must remain enrolled while "
                        + "controller handshakes are accepted."));
            }

            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                var created = StationControllerHandshake.Create(stationId, report);
                var recoveryReason = RecoveryReason(
                    previous: null,
                    report,
                    lifecycle.Station);
                var initialKind = StationControllerHandshakeFactKind.Reported;
                if (recoveryReason is not null)
                {
                    var recovery = created.RequireRecovery(
                        actorId,
                        recoveryReason,
                        receivedAtUtc);
                    if (!recovery.Succeeded)
                    {
                        return Conflict(recovery);
                    }

                    initialKind =
                        StationControllerHandshakeFactKind.RecoveryRequired;
                }

                var initialFact = created.CreateFact(
                    sequence: 1,
                    initialKind,
                    actorId,
                    recoveryReason is null
                        ? reason
                        : $"{reason}; {recoveryReason}",
                    receivedAtUtc);
                if (await repository.TryAddAsync(
                        created,
                        initialFact,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return await PostValidateReportingLeaseAsync(
                            stationId,
                            input,
                            actorId,
                            new StationControllerHandshakePersistenceEntry(
                                created,
                                revision: 0),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            var recoveryCause = RecoveryReason(
                entry.State.LatestReport,
                report,
                lifecycle.Station);
            var mutation = entry.State.Report(
                report,
                allowCommandIdentityChangeForRecovery:
                    recoveryCause is not null);
            if (!mutation.Succeeded)
            {
                return Conflict(mutation);
            }

            if (recoveryCause is not null && !entry.State.RecoveryRequired)
            {
                var recovery = entry.State.RequireRecovery(
                    actorId,
                    recoveryCause,
                    receivedAtUtc);
                if (!recovery.Succeeded)
                {
                    return Conflict(recovery);
                }

                if (recovery.Changed)
                {
                    mutation = StationControllerHandshakeMutation.Accepted(
                        changed: true,
                        recovery.Message,
                        StationControllerHandshakeFactKind.RecoveryRequired);
                }
            }

            if (!mutation.Changed)
            {
                return Result.Success(entry);
            }

            var fact = mutation.FactKind is { } factKind
                ? entry.State.CreateFact(
                    checked(entry.Revision + 2),
                    factKind,
                    actorId,
                    recoveryCause is null
                        ? reason
                        : $"{reason}; {recoveryCause}",
                    receivedAtUtc)
                : null;
            try
            {
                var revision = await repository.SaveAsync(
                        entry.State,
                        entry.Revision,
                        fact,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await PostValidateReportingLeaseAsync(
                        stationId,
                        input,
                        actorId,
                        new StationControllerHandshakePersistenceEntry(
                            entry.State,
                            revision),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (StationControllerHandshakeConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
            catch (StationControllerHandshakeConcurrencyException)
            {
                return ConcurrencyConflict(stationId);
            }
        }

        return ConcurrencyConflict(stationId);
    }

    public async ValueTask<Result<StationControllerHandshakePersistenceEntry>>
        AcknowledgeRecoveryAsync(
            StationId stationId,
            long expectedRecoveryEpoch,
            string expectedControllerSessionId,
            string expectedOperationalStateSha256,
            string actorId,
            string reason,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRecoveryEpoch, 1);
        _ = Required(
            expectedControllerSessionId,
            nameof(expectedControllerSessionId));
        _ = RequiredSha256(
            expectedOperationalStateSha256,
            nameof(expectedOperationalStateSha256));
        _ = Required(actorId, nameof(actorId));
        _ = Required(reason, nameof(reason));

        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return NotFound(stationId);
            }

            if (entry.State.RecoveryEpoch != expectedRecoveryEpoch
                || !string.Equals(
                    entry.State.LatestReport.ControllerSessionId,
                    expectedControllerSessionId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    StationControllerHandshakeEvidence.OperationalStateSha256(
                        entry.State),
                    expectedOperationalStateSha256,
                    StringComparison.Ordinal))
            {
                return Result.Failure<StationControllerHandshakePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerHandshakeRecoveryIntentStale",
                        "Controller recovery acknowledgement does not match the "
                        + "reviewed recovery epoch, session, and operational state."));
            }

            var lifecycle = await lifecycleRepository.GetByIdAsync(
                    stationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lifecycle is null)
            {
                return Result.Failure<StationControllerHandshakePersistenceEntry>(
                    ApplicationError.NotFound(
                        "Runtime.StationLifecycleNotFound",
                        $"Station lifecycle {stationId} was not found."));
            }

            if (lifecycle.Station.PendingControllerRecovery is not null)
            {
                return Result.Failure<StationControllerHandshakePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerRecoverySynchronizationPending",
                        "Controller recovery acknowledgement is blocked until the "
                        + "durable Station recovery intent has synchronized."));
            }

            if (lifecycle.Station.State is not StationState.Stopped
                and not StationState.Idle
                and not StationState.Aborted)
            {
                return Result.Failure<StationControllerHandshakePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerHandshakeRecoveryLifecycleActive",
                        $"Controller recovery cannot be acknowledged while Station "
                        + $"state is {lifecycle.Station.State}; stop or abort the "
                        + "active lifecycle first."));
            }

            var readiness = StationControllerHandshakeReadinessEvaluator.Evaluate(
                entry.State,
                clock.UtcNow,
                options);
            if (!readiness.Allowed
                && !string.Equals(
                    readiness.Code,
                    "Runtime.StationControllerHandshakeRecoveryRequired",
                    StringComparison.Ordinal))
            {
                return Result.Failure<StationControllerHandshakePersistenceEntry>(
                    ApplicationError.Conflict(readiness.Code, readiness.Reason));
            }

            var report = entry.State.LatestReport;
            var leaseValidation =
                await agentControlLeaseRepository.ValidateGenerationAsync(
                        stationId,
                        report.OwnerAgentId,
                        report.OwnerAgentInstanceId,
                        report.AgentFencingToken,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!leaseValidation.IsValid)
            {
                return Result.Failure<
                    StationControllerHandshakePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationAgentControlLeaseInvalid",
                        "Controller recovery cannot be acknowledged without "
                        + "the exact active Agent control lease generation."));
            }

            var recipeConfirmationRequired =
                lifecycle.Station.State == StationState.Idle
                && lifecycle.Station.Mode == StationMode.Automatic
                && lifecycle.Station.Readiness.RecipeVerified;
            if (report.Error
                || report.Busy
                || report.CommandSequence != report.AcknowledgedCommandSequence
                || (recipeConfirmationRequired && !report.RecipeConfirmed)
                || !report.SafetyPermitGranted
                || report.ObservedMode != lifecycle.Station.Mode
                || report.ObservedState != lifecycle.Station.State)
            {
                return Result.Failure<StationControllerHandshakePersistenceEntry>(
                    ApplicationError.Conflict(
                        "Runtime.StationControllerHandshakeRecoveryUnsafe",
                        "Recovery can be acknowledged only while the controller is idle, "
                        + "error-free, command-synchronized, lifecycle-aligned, "
                        + "appropriately recipe-confirmed, and safety-permitted."));
            }

            var acknowledgedAtUtc = clock.UtcNow;
            var mutation = entry.State.AcknowledgeRecovery(
                actorId,
                reason,
                acknowledgedAtUtc);
            if (!mutation.Succeeded)
            {
                return Conflict(mutation);
            }

            if (!mutation.Changed)
            {
                return Result.Success(entry);
            }

            var fact = entry.State.CreateFact(
                checked(entry.Revision + 2),
                mutation.FactKind!.Value,
                actorId,
                reason,
                acknowledgedAtUtc);
            try
            {
                var revision = await repository.SaveAsync(
                        entry.State,
                        entry.Revision,
                        fact,
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Success(
                    new StationControllerHandshakePersistenceEntry(
                        entry.State,
                        revision));
            }
            catch (StationControllerHandshakeConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
            catch (StationControllerHandshakeConcurrencyException)
            {
                return ConcurrencyConflict(stationId);
            }
        }

        return ConcurrencyConflict(stationId);
    }

    private async ValueTask<
        Result<StationControllerHandshakePersistenceEntry>>
        PostValidateReportingLeaseAsync(
            StationId stationId,
            StationControllerHandshakeInput input,
            string actorId,
            StationControllerHandshakePersistenceEntry persisted,
            CancellationToken cancellationToken)
    {
        var validation = await agentControlLeaseValidator.ValidateAsync(
                stationId,
                actorId,
                input.OwnerAgentInstanceId,
                input.AgentFencingToken,
                input.LeaseHandle,
                cancellationToken)
            .ConfigureAwait(false);
        if (validation.IsValid)
        {
            return Result.Success(persisted);
        }

        var recoveryPersisted = await RequireRecoveryAfterLeaseLossAsync(
                stationId,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Failure<StationControllerHandshakePersistenceEntry>(
            ApplicationError.Conflict(
                "Runtime.StationControllerHandshakeAgentFenceLost",
                recoveryPersisted
                    ? "The Agent control lease changed while the controller "
                        + "report was persisted; explicit recovery is required."
                    : "The Agent control lease changed while the controller "
                        + "report was persisted and recovery persistence did "
                        + "not converge."));
    }

    private async ValueTask<bool> RequireRecoveryAfterLeaseLossAsync(
        StationId stationId,
        CancellationToken cancellationToken)
    {
        const string actorId = "system.agent-control-fence";
        const string reason =
            "Agent control lease changed while controller evidence was persisted.";
        for (var attempt = 1; attempt <= MaximumConcurrencyAttempts; attempt++)
        {
            var entry = await repository.GetByIdAsync(stationId, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            var occurredAtUtc = clock.UtcNow;
            var mutation = entry.State.RequireRecovery(
                actorId,
                reason,
                occurredAtUtc);
            if (!mutation.Succeeded)
            {
                return false;
            }

            if (!mutation.Changed)
            {
                return true;
            }

            var fact = entry.State.CreateFact(
                checked(entry.Revision + 2),
                mutation.FactKind!.Value,
                actorId,
                reason,
                occurredAtUtc);
            try
            {
                _ = await repository.SaveAsync(
                        entry.State,
                        entry.Revision,
                        fact,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (StationControllerHandshakeConcurrencyException)
                when (attempt < MaximumConcurrencyAttempts)
            {
            }
        }

        return false;
    }

    private static StationControllerHandshakeReport ToReport(
        StationControllerHandshakeInput input,
        string ownerAgentId,
        DateTimeOffset receivedAtUtc) =>
        new(
            input.ControllerSessionId,
            input.HeartbeatSequence,
            input.CommandSequence,
            input.AcknowledgedCommandSequence,
            input.Busy,
            input.Completed,
            input.Error,
            input.ErrorCode,
            input.RecipeConfirmed,
            input.ConfirmedRecipeId,
            input.ConfirmedRecipeVersion,
            input.SafetyPermitGranted,
            input.SourceTimestampUtc,
            receivedAtUtc,
            ownerAgentId,
            input.OwnerAgentInstanceId,
            input.AgentFencingToken,
            input.CommandId,
            input.CommandFencingToken,
            input.ObservedMode,
            input.ObservedState,
            input.StateSequence);

    private static string? RecoveryReason(
        StationControllerHandshakeReport? previous,
        StationControllerHandshakeReport report,
        StationLifecycle lifecycle)
    {
        if (report.Error)
        {
            return $"Controller reported error {report.ErrorCode}; explicit "
                + "recovery is required.";
        }

        if (!report.SafetyPermitGranted)
        {
            return "Controller safety permit was denied; explicit recovery is required.";
        }

        if (previous is not null
            && (report.AgentFencingToken != previous.AgentFencingToken
                || !string.Equals(
                    report.OwnerAgentId,
                    previous.OwnerAgentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    report.OwnerAgentInstanceId,
                    previous.OwnerAgentInstanceId,
                    StringComparison.Ordinal)))
        {
            return "The reporting Agent lease generation changed; explicit "
                + "controller recovery is required.";
        }

        if (previous is not null
            && report.StateSequence - previous.StateSequence > 1)
        {
            return $"Controller operational state sequence advanced from "
                + $"{previous.StateSequence} to {report.StateSequence}; "
                + "intermediate state evidence is missing.";
        }

        if (previous is null && report.CommandSequence != 0)
        {
            return $"The first observed controller report already contains command "
                + $"sequence {report.CommandSequence}; prior physical completion is "
                + "unknown.";
        }

        if (previous is not null
            && CommandEvidenceChanged(previous, report)
            && !MatchesPendingCommand(lifecycle, report))
        {
            return $"Controller command evidence {report.CommandId}/"
                + $"{report.CommandSequence}/{report.CommandFencingToken} does not "
                + "match the Station's pending command.";
        }

        if (!ObservedStateIsCoherent(lifecycle, report))
        {
            return $"Controller observed {report.ObservedMode}/{report.ObservedState}, "
                + $"which is inconsistent with Station lifecycle "
                + $"{lifecycle.Mode}/{lifecycle.State}.";
        }

        if (lifecycle.State == StationState.Execute && !report.RecipeConfirmed)
        {
            return "Controller recipe confirmation was lost during Execute.";
        }

        return null;
    }

    private static bool CommandEvidenceChanged(
        StationControllerHandshakeReport previous,
        StationControllerHandshakeReport report) =>
        previous.CommandSequence != report.CommandSequence
        || previous.AcknowledgedCommandSequence
            != report.AcknowledgedCommandSequence
        || !string.Equals(
            previous.CommandId,
            report.CommandId,
            StringComparison.Ordinal)
        || previous.CommandFencingToken != report.CommandFencingToken;

    private static bool MatchesPendingCommand(
        StationLifecycle lifecycle,
        StationControllerHandshakeReport report) =>
        lifecycle.PendingControllerCommand is { } pending
        && lifecycle.PendingControllerCommandDelivery is { } delivery
        && string.Equals(
            delivery.CommandId,
            pending.CommandId,
            StringComparison.Ordinal)
        && string.Equals(
            delivery.OwnerAgentId,
            pending.OwnerAgentId,
            StringComparison.Ordinal)
        && string.Equals(
            delivery.OwnerAgentInstanceId,
            pending.OwnerAgentInstanceId,
            StringComparison.Ordinal)
        && delivery.FencingToken == pending.FencingToken
        && string.Equals(
            pending.ControllerSessionId,
            report.ControllerSessionId,
            StringComparison.Ordinal)
        && pending.ExpectedCommandSequence == report.CommandSequence
        && string.Equals(
            pending.OwnerAgentId,
            report.OwnerAgentId,
            StringComparison.Ordinal)
        && string.Equals(
            pending.OwnerAgentInstanceId,
            report.OwnerAgentInstanceId,
            StringComparison.Ordinal)
        && pending.FencingToken == report.AgentFencingToken
        && string.Equals(
            pending.CommandId,
            report.CommandId,
            StringComparison.Ordinal)
        && pending.FencingToken == report.CommandFencingToken;

    private static bool ObservedStateIsCoherent(
        StationLifecycle lifecycle,
        StationControllerHandshakeReport report)
    {
        if (report.ObservedMode != lifecycle.Mode)
        {
            return false;
        }

        if (lifecycle.PendingControllerCommand is not { } pending)
        {
            return report.ObservedState == lifecycle.State;
        }

        var transition = lifecycle.TransitionAudit[^1];
        if (report.CommandSequence == pending.ExpectedCommandSequence - 1)
        {
            return report.ObservedState == transition.FromState;
        }

        return report.CommandSequence == pending.ExpectedCommandSequence
            && MatchesPendingCommand(lifecycle, report)
            && report.ObservedState is var observed
            && (observed == lifecycle.State
                || observed == pending.ExpectedCompletionState);
    }

    private static Result<StationControllerHandshakePersistenceEntry> Conflict(
        StationControllerHandshakeMutation mutation) =>
        Result.Failure<StationControllerHandshakePersistenceEntry>(
            ApplicationError.Conflict(mutation.Code, mutation.Message));

    private static Result<StationControllerHandshakePersistenceEntry> NotFound(
        StationId stationId) =>
        Result.Failure<StationControllerHandshakePersistenceEntry>(
            NotFoundError(stationId));

    private static ApplicationError NotFoundError(StationId stationId) =>
        ApplicationError.NotFound(
            "Runtime.StationControllerHandshakeNotFound",
            $"Station controller handshake {stationId} was not found.");

    private static Result<StationControllerHandshakePersistenceEntry>
        ConcurrencyConflict(StationId stationId) =>
        Result.Failure<StationControllerHandshakePersistenceEntry>(
            ApplicationError.Conflict(
                "Runtime.StationControllerHandshakeConcurrencyConflict",
                $"Station controller handshake {stationId} kept changing while "
                + "the request was applied; retry the request."));

    private static string Required(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be non-empty canonical text.",
                parameterName)
            : value;
    }

    private static string RequiredSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                $"{parameterName} must be a lowercase SHA-256 value.",
                parameterName);
        }

        return value;
    }
}
