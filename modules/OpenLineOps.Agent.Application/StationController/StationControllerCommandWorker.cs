using System.Collections.Concurrent;
using System.Diagnostics;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Application.StationController;

public sealed class StationControllerCommandWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RenewalLeadTime { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaximumRenewalInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan CoordinatorRequestTimeout { get; init; } =
        TimeSpan.FromSeconds(5);

    public TimeSpan PhysicalExecutionTimeout { get; init; } =
        TimeSpan.FromSeconds(30);

    public TimeSpan PersistenceTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ShutdownReleaseTimeout { get; init; } =
        TimeSpan.FromSeconds(2);

    public void Validate()
    {
        RequireRange(
            PollInterval,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(30),
            nameof(PollInterval));
        RequireRange(
            RenewalLeadTime,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(2),
            nameof(RenewalLeadTime));
        RequireRange(
            MaximumRenewalInterval,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            nameof(MaximumRenewalInterval));
        RequireRange(
            CoordinatorRequestTimeout,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(2),
            nameof(CoordinatorRequestTimeout));
        RequireRange(
            PhysicalExecutionTimeout,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(10),
            nameof(PhysicalExecutionTimeout));
        RequireRange(
            PersistenceTimeout,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(1),
            nameof(PersistenceTimeout));
        RequireRange(
            ShutdownReleaseTimeout,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            nameof(ShutdownReleaseTimeout));
    }

    private static void RequireRange(
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"{name} must be between {minimum:c} and {maximum:c}.");
        }
    }
}

public sealed class StationControllerCommandWorker(
    StationAgentProcessIdentity identity,
    IStationControllerCoordinatorClient coordinator,
    IStationControllerCommandJournal journal,
    IStationPhysicalControllerExecutor physicalController,
    IClock clock,
    StationControllerCommandWorkerOptions options,
    StationAgentControlLeaseState? leaseState = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        StationExecutionGates = new(StringComparer.Ordinal);

    private readonly StationAgentProcessIdentity _identity =
        identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly IStationControllerCoordinatorClient _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly IStationControllerCommandJournal _journal =
        journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IStationPhysicalControllerExecutor _physicalController =
        physicalController ?? throw new ArgumentNullException(nameof(physicalController));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly StationControllerCommandWorkerOptions _options =
        ValidateOptions(options);
    private readonly StationAgentControlLeaseState _leaseState =
        leaseState ?? new StationAgentControlLeaseState(identity);

    public StationAgentProcessIdentity Identity => _identity;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var lease = await WithCoordinatorTimeoutAsync(
                token => _coordinator.AcquireLeaseAsync(_identity, token),
                cancellationToken)
            .ConfigureAwait(false);
        ValidateLease(lease);
        _leaseState.ActivateOrRenew(lease);
        var lastAuthorityCheckTimestamp = Stopwatch.GetTimestamp();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var nowUtc = UtcNow();
                if (nowUtc >= lease.ExpiresAtUtc - _options.RenewalLeadTime
                    || Stopwatch.GetElapsedTime(lastAuthorityCheckTimestamp)
                        >= _options.MaximumRenewalInterval)
                {
                    var renewedUntilUtc = await WithCoordinatorTimeoutAsync(
                            token => _coordinator.RenewLeaseAsync(
                                _identity,
                                lease,
                                token),
                            cancellationToken)
                        .ConfigureAwait(false);
                    RequireFutureUtc(renewedUntilUtc, nowUtc, "renewed lease expiry");
                    lease = lease.Renewed(renewedUntilUtc);
                    _leaseState.ActivateOrRenew(lease);
                    lastAuthorityCheckTimestamp = Stopwatch.GetTimestamp();
                }

                _ = await ProcessNextCommandAsync(lease, cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(_options.PollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _leaseState.Clear(lease);
            using var releaseTimeout = new CancellationTokenSource(
                _options.ShutdownReleaseTimeout);
            await _coordinator.ReleaseLeaseAsync(
                    _identity,
                    lease,
                    releaseTimeout.Token)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> ProcessNextCommandAsync(
        StationAgentControlLeaseGrant lease,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        var command = await WithCoordinatorTimeoutAsync(
                token => _coordinator.PollCommandAsync(
                    _identity,
                    lease,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        if (command is null)
        {
            return false;
        }

        await ProcessCommandAsync(lease, command, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async ValueTask ProcessCommandAsync(
        StationAgentControlLeaseGrant lease,
        StationControllerCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        ValidateLease(lease);
        ValidateCommand(command, lease);
        var executionGate = StationExecutionGates.GetOrAdd(
            _identity.StationId,
            static _ => new SemaphoreSlim(1, 1));
        await executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProcessCommandCoreAsync(lease, command, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            executionGate.Release();
        }
    }

    private async ValueTask ProcessCommandCoreAsync(
        StationAgentControlLeaseGrant lease,
        StationControllerCommandEnvelope command,
        CancellationToken cancellationToken)
    {
        var commandSha256 = StationControllerCommandFingerprint.Compute(command);
        var acceptance = await WithPersistenceTimeoutAsync(
                token => _journal.TryAcceptAsync(
                    command,
                    commandSha256,
                    UtcNow(),
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        var entry = acceptance.Status switch
        {
            StationControllerCommandAcceptanceStatus.Accepted
                or StationControllerCommandAcceptanceStatus.Existing =>
                acceptance.Entry ?? throw new InvalidDataException(
                    "Accepted command journal result did not include an entry."),
            StationControllerCommandAcceptanceStatus.StaleFencingToken =>
                throw new StationAgentControlLeaseRejectedException(
                    $"Controller command fencing token {command.FencingToken} is "
                    + $"below durable station high-water "
                    + $"{acceptance.FencingTokenHighWater}."),
            StationControllerCommandAcceptanceStatus.FencingOwnerMismatch =>
                throw new StationAgentControlLeaseRejectedException(
                    "Controller command fencing token is owned by another Agent boot."),
            StationControllerCommandAcceptanceStatus.CommandIdentityConflict =>
                throw new InvalidDataException(
                    $"Controller command id '{command.CommandId}' was reused with "
                    + "different immutable evidence."),
            _ => throw new InvalidDataException(
                $"Unsupported command acceptance status {acceptance.Status}.")
        };

        switch (entry.Status)
        {
            case StationControllerCommandJournalStatus.Completed:
            case StationControllerCommandJournalStatus.Failed:
            case StationControllerCommandJournalStatus.CompletionUnknown:
            case StationControllerCommandJournalStatus.Rejected:
                await ReplayTerminalAsync(lease, entry, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case StationControllerCommandJournalStatus.Invoking
                when command.Idempotency
                    is not StationControllerCommandIdempotency.Idempotent:
                entry = await PersistInterruptedUnknownAsync(entry)
                    .ConfigureAwait(false);
                await ReplayTerminalAsync(lease, entry, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case StationControllerCommandJournalStatus.Accepted:
            case StationControllerCommandJournalStatus.Invoking:
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported command journal status {entry.Status}.");
        }

        var nowUtc = UtcNow();
        if (nowUtc >= command.DeadlineUtc)
        {
            var observation = await TryObserveControllerAsync()
                .ConfigureAwait(false);
            var rejected = await CompleteDurablyAsync(
                    entry,
                    StationControllerCommandJournalStatus.Rejected,
                    new StationPhysicalControllerExecutionResult(
                        StationPhysicalControllerExecutionOutcome.Failed,
                        observation,
                        "Agent.ControllerCommandDeadlineExpired",
                        "The controller command deadline expired before physical invocation."),
                    nowUtc)
                .ConfigureAwait(false);
            await ReplayTerminalAsync(lease, rejected, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        lease = await RenewForInvocationAsync(lease, cancellationToken)
            .ConfigureAwait(false);
        var invocationClaim = await WithPersistenceTimeoutAsync(
                token => _journal.MarkInvokingAsync(
                    command.StationId,
                    command.CommandId,
                    commandSha256,
                    entry.Status,
                    entry.InvocationAttemptCount,
                    JournalTimestamp(entry, UtcNow()),
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        entry = invocationClaim.Entry;
        if (!invocationClaim.Claimed)
        {
            if (IsTerminal(entry.Status))
            {
                await ReplayTerminalAsync(lease, entry, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        lease = await RenewForInvocationAsync(lease, cancellationToken)
            .ConfigureAwait(false);
        var invocation = await InvokePhysicalControllerAsync(
                lease,
                command,
                cancellationToken)
            .ConfigureAwait(false);
        lease = invocation.Lease;
        var result = ReconcilePhysicalResult(command, invocation.Result);
        var terminalStatus = result.Outcome switch
        {
            StationPhysicalControllerExecutionOutcome.Completed =>
                StationControllerCommandJournalStatus.Completed,
            StationPhysicalControllerExecutionOutcome.Failed =>
                StationControllerCommandJournalStatus.Failed,
            StationPhysicalControllerExecutionOutcome.CompletionUnknown =>
                StationControllerCommandJournalStatus.CompletionUnknown,
            _ => throw new InvalidDataException(
                $"Unsupported physical result outcome {result.Outcome}.")
        };
        entry = await CompleteDurablyAsync(
                entry,
                terminalStatus,
                result,
                UtcNow())
            .ConfigureAwait(false);
        await ReplayTerminalAsync(lease, entry, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<PhysicalInvocation>
        InvokePhysicalControllerAsync(
            StationAgentControlLeaseGrant lease,
            StationControllerCommandEnvelope command,
            CancellationToken cancellationToken)
    {
        var remaining = command.DeadlineUtc - UtcNow();
        var timeout = remaining < _options.PhysicalExecutionTimeout
            ? remaining
            : _options.PhysicalExecutionTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            return new PhysicalInvocation(
                new StationPhysicalControllerExecutionResult(
                    StationPhysicalControllerExecutionOutcome.Failed,
                    null,
                    "Agent.ControllerCommandDeadlineExpired",
                    "The controller command deadline expired before physical invocation."),
                lease);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<StationPhysicalControllerExecutionResult>? executionTask = null;
        try
        {
            executionTask = _physicalController.ExecuteAsync(
                    command,
                    timeoutSource.Token)
                .AsTask();
            while (!executionTask.IsCompleted)
            {
                var renewAtUtc = lease.ExpiresAtUtc - _options.RenewalLeadTime;
                var delay = renewAtUtc - UtcNow();
                if (delay > _options.MaximumRenewalInterval)
                {
                    delay = _options.MaximumRenewalInterval;
                }
                if (delay > TimeSpan.Zero)
                {
                    var delayTask = Task.Delay(delay, timeoutSource.Token);
                    if (await Task.WhenAny(executionTask, delayTask)
                            .ConfigureAwait(false) == executionTask)
                    {
                        break;
                    }

                    await delayTask.ConfigureAwait(false);
                }

                var renewedUntilUtc = await WithCoordinatorTimeoutAsync(
                        token => _coordinator.RenewLeaseAsync(
                            _identity,
                            lease,
                            token),
                        timeoutSource.Token)
                    .ConfigureAwait(false);
                RequireFutureUtc(
                    renewedUntilUtc,
                    UtcNow(),
                    "physical invocation lease expiry");
                lease = lease.Renewed(renewedUntilUtc);
                _leaseState.ActivateOrRenew(lease);
            }

            var result = await executionTask.ConfigureAwait(false);
            if (result.Outcome
                    != StationPhysicalControllerExecutionOutcome.Completed
                && result.Observation is null)
            {
                result = result with
                {
                    Observation = await TryObserveControllerAsync()
                        .ConfigureAwait(false)
                };
            }

            ValidatePhysicalResult(command, result);
            return new PhysicalInvocation(result, lease);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
            and not OutOfMemoryException)
        {
            await timeoutSource.CancelAsync().ConfigureAwait(false);
            if (executionTask is not null && !executionTask.IsCompleted)
            {
                _ = ObserveLatePhysicalTaskAsync(executionTask);
            }

            var observation = await TryObserveControllerAsync()
                .ConfigureAwait(false);
            return new PhysicalInvocation(
                new StationPhysicalControllerExecutionResult(
                    StationPhysicalControllerExecutionOutcome.CompletionUnknown,
                    observation,
                    "Agent.ControllerCommandCompletionUnknown",
                    $"Physical controller invocation ended without conclusive completion "
                    + $"evidence ({exception.GetType().Name})."),
                lease);
        }
    }

    private static async Task ObserveLatePhysicalTaskAsync(
        Task<StationPhysicalControllerExecutionResult> executionTask)
    {
        try
        {
            _ = await executionTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
            and not OutOfMemoryException)
        {
        }
    }

    private async ValueTask<StationAgentControlLeaseGrant> RenewForInvocationAsync(
        StationAgentControlLeaseGrant lease,
        CancellationToken cancellationToken)
    {
        var renewedUntilUtc = await WithCoordinatorTimeoutAsync(
                token => _coordinator.RenewLeaseAsync(
                    _identity,
                    lease,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        RequireFutureUtc(
            renewedUntilUtc,
            UtcNow(),
            "pre-invocation lease expiry");
        var renewed = lease.Renewed(renewedUntilUtc);
        _leaseState.ActivateOrRenew(renewed);
        return renewed;
    }

    private async ValueTask<StationControllerCommandJournalEntry>
        PersistInterruptedUnknownAsync(StationControllerCommandJournalEntry entry)
    {
        var observation = await TryObserveControllerAsync()
            .ConfigureAwait(false);
        var result = ReconcilePhysicalResult(
            entry.Command,
            new StationPhysicalControllerExecutionResult(
                StationPhysicalControllerExecutionOutcome.CompletionUnknown,
                observation,
                "Agent.ControllerCommandRecoveryRequired",
                "A prior process persisted physical invocation intent but did "
                + "not persist conclusive completion evidence; the command was not replayed."));
        var status = result.Outcome switch
        {
            StationPhysicalControllerExecutionOutcome.Completed =>
                StationControllerCommandJournalStatus.Completed,
            StationPhysicalControllerExecutionOutcome.Failed =>
                StationControllerCommandJournalStatus.Failed,
            _ => StationControllerCommandJournalStatus.CompletionUnknown
        };
        return await CompleteDurablyAsync(
                entry,
                status,
                result,
                UtcNow())
            .ConfigureAwait(false);
    }

    private async ValueTask<StationControllerObservation?>
        TryObserveControllerAsync()
    {
        using var timeout = new CancellationTokenSource(
            _options.CoordinatorRequestTimeout);
        try
        {
            var observation = await _physicalController.ObserveAsync(
                    _identity.StationId,
                    timeout.Token)
                .ConfigureAwait(false);
            ValidateObservation(observation);
            return observation;
        }
        catch (Exception exception) when (
            exception is not StackOverflowException
            and not OutOfMemoryException)
        {
            return null;
        }
    }

    private async ValueTask<StationControllerCommandJournalEntry> CompleteDurablyAsync(
        StationControllerCommandJournalEntry entry,
        StationControllerCommandJournalStatus status,
        StationPhysicalControllerExecutionResult result,
        DateTimeOffset terminalAtUtc)
    {
        terminalAtUtc = JournalTimestamp(entry, terminalAtUtc);
        using var timeout = new CancellationTokenSource(_options.PersistenceTimeout);
        return await _journal.CompleteAsync(
                entry.Command.StationId,
                entry.Command.CommandId,
                entry.CommandSha256,
                status,
                result,
                terminalAtUtc,
                timeout.Token)
            .ConfigureAwait(false);
    }

    private static DateTimeOffset JournalTimestamp(
        StationControllerCommandJournalEntry entry,
        DateTimeOffset candidateUtc)
    {
        var minimumUtc = entry.InvocationStartedAtUtc is { } invocationStartedAtUtc
            && invocationStartedAtUtc > entry.AcceptedAtUtc
                ? invocationStartedAtUtc
                : entry.AcceptedAtUtc;
        return candidateUtc < minimumUtc ? minimumUtc : candidateUtc;
    }

    private static bool IsTerminal(StationControllerCommandJournalStatus status) =>
        status is StationControllerCommandJournalStatus.Completed
            or StationControllerCommandJournalStatus.Failed
            or StationControllerCommandJournalStatus.CompletionUnknown
            or StationControllerCommandJournalStatus.Rejected;

    private async ValueTask ReplayTerminalAsync(
        StationAgentControlLeaseGrant lease,
        StationControllerCommandJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var result = entry.Result;
        if (result?.Observation is not { } observation)
        {
            return;
        }

        ValidatePhysicalResult(entry.Command, result);

        if (observation.CommandSequence != entry.Command.CommandSequence)
        {
            return;
        }

        var completed = entry.Status == StationControllerCommandJournalStatus.Completed;
        var report = new StationControllerHandshakeReport(
            _identity.OwnerInstanceId,
            lease.FencingToken,
            observation.ControllerSessionId,
            observation.HeartbeatSequence,
            observation.CommandSequence,
            observation.AcknowledgedCommandSequence,
            observation.Busy,
            observation.Completed,
            observation.Error,
            observation.ErrorCode,
            observation.RecipeConfirmed,
            observation.ConfirmedRecipeId,
            observation.ConfirmedRecipeVersion,
            observation.SafetyPermitGranted,
            observation.ObservedAtUtc,
            entry.Command.CommandId,
            entry.Command.FencingToken,
            observation.ObservedMode,
            observation.ObservedState,
            observation.StateSequence,
            ReportReason(entry));
        await WithCoordinatorTimeoutAsync(
                async token =>
                {
                    await _coordinator.ReportHandshakeAsync(
                            _identity,
                            lease,
                            report,
                            token)
                        .ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!completed)
        {
            return;
        }

        var acknowledgement = new StationControllerCommandAcknowledgement(
            _identity.OwnerInstanceId,
            lease.FencingToken,
            entry.Command.CommandId,
            observation.ControllerSessionId,
            entry.Command.CommandSequence,
            observation.ObservedMode,
            observation.ObservedState,
            observation.StateSequence,
            $"Controller command {entry.Command.CommandId} completed with durable evidence.");
        await WithCoordinatorTimeoutAsync(
                async token =>
                {
                    await _coordinator.AcknowledgeCommandAsync(
                            _identity,
                            lease,
                            acknowledgement,
                            token)
                        .ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ReportReason(StationControllerCommandJournalEntry entry) =>
        entry.Status switch
        {
            StationControllerCommandJournalStatus.Completed =>
                $"Controller command {entry.Command.CommandId} completed with durable evidence.",
            StationControllerCommandJournalStatus.Failed =>
                $"Controller command {entry.Command.CommandId} failed with durable evidence.",
            StationControllerCommandJournalStatus.CompletionUnknown =>
                $"Controller command {entry.Command.CommandId} requires physical reconciliation.",
            StationControllerCommandJournalStatus.Rejected =>
                $"Controller command {entry.Command.CommandId} was rejected before invocation.",
            _ => throw new InvalidDataException(
                $"Command journal status {entry.Status} is not terminal.")
        };

    private void ValidateLease(StationAgentControlLeaseGrant lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(lease.StationId, _identity.StationId, StringComparison.Ordinal)
            || !string.Equals(
                lease.OwnerInstanceId,
                _identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                lease.LeaseHandle,
                _identity.LeaseHandle,
                StringComparison.Ordinal))
        {
            throw new StationAgentControlLeaseRejectedException(
                "Control lease does not belong to this Agent process identity.");
        }
    }

    private void ValidateCommand(
        StationControllerCommandEnvelope command,
        StationAgentControlLeaseGrant lease)
    {
        ArgumentNullException.ThrowIfNull(command);
        Required(command.CommandId, nameof(command.CommandId));
        Required(command.ControllerSessionId, nameof(command.ControllerSessionId));
        Required(command.Trigger, nameof(command.Trigger));
        Required(command.ExpectedMode, nameof(command.ExpectedMode));
        Required(command.ExpectedCompletionState, nameof(command.ExpectedCompletionState));
        Required(command.SafetyClass, nameof(command.SafetyClass));
        if (command.SafetyClass is not "Operational" and not "SafetyRelevant")
        {
            throw new InvalidDataException(
                $"Controller command safety class '{command.SafetyClass}' is unsupported.");
        }

        if ((command.ConfirmedRecipeId is null)
            != (command.ConfirmedRecipeVersion is null))
        {
            throw new InvalidDataException(
                "Controller command recipe identity and version must both be "
                + "present or both be absent.");
        }

        if (command.ConfirmedRecipeId is not null)
        {
            Required(command.ConfirmedRecipeId, nameof(command.ConfirmedRecipeId));
            Required(
                command.ConfirmedRecipeVersion!,
                nameof(command.ConfirmedRecipeVersion));
        }

        var hasRecipeAuthority = command.RecipeAssignmentId is not null
            || command.RecipeDeploymentId is not null
            || command.RecipeConfigurationSha256 is not null;
        if (string.Equals(command.Trigger, "Start", StringComparison.Ordinal))
        {
            if (!hasRecipeAuthority
                || command.RecipeAssignmentId is null
                || command.RecipeDeploymentId is null
                || command.RecipeConfigurationSha256 is null)
            {
                throw new InvalidDataException(
                    "Start controller commands require exact recipe assignment, "
                    + "deployment, and configuration authority.");
            }

            RequireCanonicalGuid(
                command.RecipeAssignmentId,
                nameof(command.RecipeAssignmentId));
            RequireCanonicalGuid(
                command.RecipeDeploymentId,
                nameof(command.RecipeDeploymentId));
            RequireSha256(
                command.RecipeConfigurationSha256,
                nameof(command.RecipeConfigurationSha256));
        }
        else if (hasRecipeAuthority)
        {
            throw new InvalidDataException(
                "Only Start controller commands may carry recipe startup authority.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(command.CommandSequence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.FencingToken, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            command.IssuedOperationalEpoch,
            1);
        if (command.ContractVersion
            != StationControllerCommandFingerprint.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported controller command contract version "
                + $"{command.ContractVersion}.");
        }
        if (!Enum.IsDefined(command.Idempotency))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command.Idempotency,
                "Controller command idempotency is invalid.");
        }

        RequireUtc(command.IssuedAtUtc, nameof(command.IssuedAtUtc));
        RequireUtc(command.DeadlineUtc, nameof(command.DeadlineUtc));
        if (command.DeadlineUtc <= command.IssuedAtUtc)
        {
            throw new ArgumentException(
                "Controller command deadline must follow issue time.",
                nameof(command));
        }

        if (!string.Equals(command.StationId, _identity.StationId, StringComparison.Ordinal)
            || !string.Equals(command.OwnerAgentId, _identity.AgentId, StringComparison.Ordinal)
            || !string.Equals(
                command.OwnerInstanceId,
                _identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || command.FencingToken != lease.FencingToken)
        {
            throw new StationAgentControlLeaseRejectedException(
                "Controller command owner or fencing token does not match the "
                + "active Agent process lease.");
        }
    }

    private static void ValidatePhysicalResult(
        StationControllerCommandEnvelope command,
        StationPhysicalControllerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.IsDefined(result.Outcome))
        {
            throw new InvalidDataException("Physical controller outcome is invalid.");
        }

        if (result.Outcome == StationPhysicalControllerExecutionOutcome.Completed
            && result.Observation is null)
        {
            throw new InvalidDataException(
                "Completed physical controller result requires observation evidence.");
        }

        if (result.Outcome == StationPhysicalControllerExecutionOutcome.Completed)
        {
            if (result.ErrorCode is not null || result.ErrorReason is not null)
            {
                throw new InvalidDataException(
                    "Completed physical controller result cannot contain error evidence.");
            }
        }
        else
        {
            Required(result.ErrorCode!, nameof(result.ErrorCode));
            Required(result.ErrorReason!, nameof(result.ErrorReason));
        }

        if (result.Observation is not { } observation)
        {
            return;
        }

        ValidateObservation(observation);
        if (result.Outcome == StationPhysicalControllerExecutionOutcome.Completed
            && !HasExactCompletionEvidence(command, observation))
        {
            throw new InvalidDataException(
                "Completed physical controller result does not match the exact "
                + "controller session, command sequence, mode, completion state, "
                + "safety permit, and recipe evidence.");
        }
    }

    private static StationPhysicalControllerExecutionResult ReconcilePhysicalResult(
        StationControllerCommandEnvelope command,
        StationPhysicalControllerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Observation is not { } observation)
        {
            ValidatePhysicalResult(command, result);
            return result;
        }

        ValidateObservation(observation);
        if (observation.Completed
            && HasExactCompletionEvidence(command, observation))
        {
            return new StationPhysicalControllerExecutionResult(
                StationPhysicalControllerExecutionOutcome.Completed,
                observation,
                ErrorCode: null,
                ErrorReason: null);
        }

        if (observation.Error)
        {
            return new StationPhysicalControllerExecutionResult(
                StationPhysicalControllerExecutionOutcome.Failed,
                observation,
                observation.ErrorCode,
                result.ErrorReason
                    ?? "The physical controller reported an error state.");
        }

        var reconciled = result.Outcome
                == StationPhysicalControllerExecutionOutcome.Completed
            || result.Outcome == StationPhysicalControllerExecutionOutcome.Failed
                ? new StationPhysicalControllerExecutionResult(
                    StationPhysicalControllerExecutionOutcome.CompletionUnknown,
                    observation,
                    "Agent.ControllerCommandCompletionUnknown",
                    result.ErrorReason
                        ?? "Controller observation did not prove a terminal outcome.")
                : result;
        ValidatePhysicalResult(command, reconciled);
        return reconciled;
    }

    private static bool HasExactCompletionEvidence(
        StationControllerCommandEnvelope command,
        StationControllerObservation observation) =>
        observation.CommandSequence == command.CommandSequence
        && observation.AcknowledgedCommandSequence == command.CommandSequence
        && observation.Completed
        && string.Equals(
            observation.ControllerSessionId,
            command.ControllerSessionId,
            StringComparison.Ordinal)
        && string.Equals(
            observation.ObservedMode,
            command.ExpectedMode,
            StringComparison.Ordinal)
        && string.Equals(
            observation.ObservedState,
            command.ExpectedCompletionState,
            StringComparison.Ordinal)
        && observation.SafetyPermitGranted
        && RecipeMatches(command, observation);

    private static bool RecipeMatches(
        StationControllerCommandEnvelope command,
        StationControllerObservation observation)
    {
        var identityMatches = command.ConfirmedRecipeId is null
            ? command.ConfirmedRecipeVersion is null
            : observation.RecipeConfirmed
                && string.Equals(
                    observation.ConfirmedRecipeId,
                    command.ConfirmedRecipeId,
                    StringComparison.Ordinal)
                && string.Equals(
                    observation.ConfirmedRecipeVersion,
                    command.ConfirmedRecipeVersion,
                    StringComparison.Ordinal);
        return identityMatches
            && (!string.Equals(command.Trigger, "Start", StringComparison.Ordinal)
                || string.Equals(
                    observation.RecipeAssignmentId,
                    command.RecipeAssignmentId,
                    StringComparison.Ordinal)
                && string.Equals(
                    observation.RecipeDeploymentId,
                    command.RecipeDeploymentId,
                    StringComparison.Ordinal)
                && string.Equals(
                    observation.RecipeConfigurationSha256,
                    command.RecipeConfigurationSha256,
                    StringComparison.Ordinal));
    }

    private static void ValidateObservation(StationControllerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Required(
            observation.ControllerSessionId,
            nameof(observation.ControllerSessionId));
        ArgumentOutOfRangeException.ThrowIfLessThan(observation.HeartbeatSequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(observation.CommandSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(
            observation.AcknowledgedCommandSequence);
        if (observation.AcknowledgedCommandSequence > observation.CommandSequence)
        {
            throw new InvalidDataException(
                "Controller acknowledgement sequence cannot exceed its command sequence.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(observation.StateSequence, 1);
        var assertedControllerStates = (observation.Busy ? 1 : 0)
            + (observation.Completed ? 1 : 0)
            + (observation.Error ? 1 : 0);
        if (assertedControllerStates > 1)
        {
            throw new InvalidDataException(
                "Busy, completed, and error controller observations are mutually exclusive.");
        }

        if (observation.Error)
        {
            Required(observation.ErrorCode!, nameof(observation.ErrorCode));
        }
        else if (observation.ErrorCode is not null)
        {
            throw new InvalidDataException(
                "Controller error code must be null when its error flag is false.");
        }

        Required(observation.ObservedMode, nameof(observation.ObservedMode));
        Required(observation.ObservedState, nameof(observation.ObservedState));
        if (observation.RecipeConfirmed)
        {
            Required(
                observation.ConfirmedRecipeId!,
                nameof(observation.ConfirmedRecipeId));
            Required(
                observation.ConfirmedRecipeVersion!,
                nameof(observation.ConfirmedRecipeVersion));
        }
        else if (observation.ConfirmedRecipeId is not null
            || observation.ConfirmedRecipeVersion is not null)
        {
            throw new InvalidDataException(
                "Unconfirmed recipe observation cannot contain recipe identity.");
        }

        var hasRecipeAuthority = observation.RecipeAssignmentId is not null
            || observation.RecipeDeploymentId is not null
            || observation.RecipeConfigurationSha256 is not null;
        if (hasRecipeAuthority)
        {
            if (observation.RecipeAssignmentId is null
                || observation.RecipeDeploymentId is null
                || observation.RecipeConfigurationSha256 is null)
            {
                throw new InvalidDataException(
                    "Controller recipe authority observation must be complete.");
            }

            RequireCanonicalGuid(
                observation.RecipeAssignmentId,
                nameof(observation.RecipeAssignmentId));
            RequireCanonicalGuid(
                observation.RecipeDeploymentId,
                nameof(observation.RecipeDeploymentId));
            RequireSha256(
                observation.RecipeConfigurationSha256,
                nameof(observation.RecipeConfigurationSha256));
        }

        RequireUtc(observation.ObservedAtUtc, nameof(observation.ObservedAtUtc));
    }

    private async ValueTask<T> WithCoordinatorTimeoutAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.CoordinatorRequestTimeout);
        return await operation(timeout.Token).ConfigureAwait(false);
    }

    private async ValueTask<T> WithPersistenceTimeoutAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.PersistenceTimeout);
        return await operation(timeout.Token).ConfigureAwait(false);
    }

    private static StationControllerCommandWorkerOptions ValidateOptions(
        StationControllerCommandWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }

    private DateTimeOffset UtcNow()
    {
        var now = _clock.UtcNow;
        return now == default || now.Offset != TimeSpan.Zero
            ? throw new InvalidOperationException(
                "Station controller command worker clock must return non-default UTC.")
            : now;
    }

    private static void RequireFutureUtc(
        DateTimeOffset value,
        DateTimeOffset nowUtc,
        string name)
    {
        RequireUtc(value, name);
        if (value <= nowUtc)
        {
            throw new StationAgentControlLeaseRejectedException(
                $"Coordinator returned an expired {name}.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName);
        }
    }

    private static void Required(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{parameterName} must be canonical text.",
                parameterName);
        }
    }

    private static void RequireCanonicalGuid(string value, string parameterName)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(
                parsed.ToString("D"),
                value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{parameterName} must be a canonical lowercase GUID.");
        }
    }

    private static void RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"{parameterName} must be lowercase SHA-256 hexadecimal.");
        }
    }

    private sealed record PhysicalInvocation(
        StationPhysicalControllerExecutionResult Result,
        StationAgentControlLeaseGrant Lease);
}
