using OpenLineOps.Agent.Application.StationController;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class InMemoryStationControllerCommandJournal :
    IStationControllerCommandJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<CommandKey, StationControllerCommandJournalEntry>
        _entries = [];
    private readonly Dictionary<string, FenceOwner> _fences =
        new(StringComparer.Ordinal);

    public ValueTask<StationControllerCommandAcceptanceResult> TryAcceptAsync(
        StationControllerCommandEnvelope command,
        string commandSha256,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateCommand(command);
        ValidateSha256(commandSha256);
        RequireUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_fences.TryGetValue(command.StationId, out var fence))
            {
                if (command.FencingToken < fence.Token)
                {
                    return ValueTask.FromResult(Acceptance(
                        StationControllerCommandAcceptanceStatus.StaleFencingToken,
                        null,
                        fence.Token));
                }

                if (command.FencingToken == fence.Token
                    && !fence.IsOwnedBy(command))
                {
                    return ValueTask.FromResult(Acceptance(
                        StationControllerCommandAcceptanceStatus.FencingOwnerMismatch,
                        null,
                        fence.Token));
                }
            }

            var key = new CommandKey(command.StationId, command.CommandId);
            if (_entries.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(Acceptance(
                    string.Equals(
                        existing.CommandSha256,
                        commandSha256,
                        StringComparison.Ordinal)
                        ? StationControllerCommandAcceptanceStatus.Existing
                        : StationControllerCommandAcceptanceStatus.CommandIdentityConflict,
                    existing,
                    _fences[command.StationId].Token));
            }

            if (!_fences.TryGetValue(command.StationId, out fence)
                || command.FencingToken > fence.Token)
            {
                fence = new FenceOwner(
                    command.FencingToken,
                    command.OwnerAgentId,
                    command.OwnerInstanceId);
                _fences[command.StationId] = fence;
            }

            var accepted = new StationControllerCommandJournalEntry(
                command,
                commandSha256,
                StationControllerCommandJournalStatus.Accepted,
                InvocationAttemptCount: 0,
                acceptedAtUtc,
                InvocationStartedAtUtc: null,
                TerminalAtUtc: null,
                Result: null);
            _entries.Add(key, accepted);
            return ValueTask.FromResult(Acceptance(
                StationControllerCommandAcceptanceStatus.Accepted,
                accepted,
                fence.Token));
        }
    }

    public ValueTask<StationControllerCommandJournalEntry?> GetAsync(
        string stationId,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        var key = Key(stationId, commandId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_entries.GetValueOrDefault(key));
        }
    }

    public ValueTask<long?> GetFencingTokenHighWaterAsync(
        string stationId,
        CancellationToken cancellationToken = default)
    {
        Required(stationId, nameof(stationId));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<long?>(
                _fences.TryGetValue(stationId, out var fence)
                    ? fence.Token
                    : null);
        }
    }

    public ValueTask<StationControllerCommandInvocationClaim> MarkInvokingAsync(
        string stationId,
        string commandId,
        string commandSha256,
        StationControllerCommandJournalStatus expectedStatus,
        int expectedInvocationAttemptCount,
        DateTimeOffset invocationStartedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var key = Key(stationId, commandId);
        ValidateSha256(commandSha256);
        RequireUtc(invocationStartedAtUtc, nameof(invocationStartedAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = ExactEntry(key, commandSha256);
            if (current.Status != expectedStatus
                || current.InvocationAttemptCount
                    != expectedInvocationAttemptCount)
            {
                return ValueTask.FromResult(
                    new StationControllerCommandInvocationClaim(false, current));
            }

            if (current.Status == StationControllerCommandJournalStatus.Invoking
                && current.Command.Idempotency
                    != StationControllerCommandIdempotency.Idempotent)
            {
                return ValueTask.FromResult(
                    new StationControllerCommandInvocationClaim(false, current));
            }

            if (current.Status is not StationControllerCommandJournalStatus.Accepted
                and not StationControllerCommandJournalStatus.Invoking)
            {
                return ValueTask.FromResult(
                    new StationControllerCommandInvocationClaim(false, current));
            }

            if (invocationStartedAtUtc < current.AcceptedAtUtc
                || current.InvocationStartedAtUtc is { } priorStartedAtUtc
                && invocationStartedAtUtc < priorStartedAtUtc)
            {
                throw new ArgumentException(
                    "Invocation time cannot precede durable command history.",
                    nameof(invocationStartedAtUtc));
            }

            var invoking = current with
            {
                Status = StationControllerCommandJournalStatus.Invoking,
                InvocationAttemptCount = checked(current.InvocationAttemptCount + 1),
                InvocationStartedAtUtc = invocationStartedAtUtc
            };
            _entries[key] = invoking;
            return ValueTask.FromResult(
                new StationControllerCommandInvocationClaim(true, invoking));
        }
    }

    public ValueTask<StationControllerCommandJournalEntry> CompleteAsync(
        string stationId,
        string commandId,
        string commandSha256,
        StationControllerCommandJournalStatus terminalStatus,
        StationPhysicalControllerExecutionResult result,
        DateTimeOffset terminalAtUtc,
        CancellationToken cancellationToken = default)
    {
        var key = Key(stationId, commandId);
        ValidateSha256(commandSha256);
        ValidateTerminalStatus(terminalStatus);
        ArgumentNullException.ThrowIfNull(result);
        ValidateTerminalResult(terminalStatus, result);
        RequireUtc(terminalAtUtc, nameof(terminalAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = ExactEntry(key, commandSha256);
            if (IsTerminal(current.Status))
            {
                if (current.Status != terminalStatus || current.Result != result)
                {
                    throw new InvalidDataException(
                        $"Command {commandId} terminal evidence does not match "
                        + "the durable journal.");
                }

                return ValueTask.FromResult(current);
            }

            if (current.Status != StationControllerCommandJournalStatus.Invoking
                && !(current.Status == StationControllerCommandJournalStatus.Accepted
                    && terminalStatus
                        == StationControllerCommandJournalStatus.Rejected))
            {
                throw new InvalidOperationException(
                    $"Command {commandId} cannot complete from {current.Status}.");
            }

            if (terminalAtUtc < current.AcceptedAtUtc)
            {
                throw new ArgumentException(
                    "Terminal time cannot precede command acceptance.",
                    nameof(terminalAtUtc));
            }

            if (current.InvocationStartedAtUtc is { } invocationStartedAtUtc
                && terminalAtUtc < invocationStartedAtUtc)
            {
                throw new ArgumentException(
                    "Terminal time cannot precede physical invocation.",
                    nameof(terminalAtUtc));
            }

            var terminal = current with
            {
                Status = terminalStatus,
                TerminalAtUtc = terminalAtUtc,
                Result = result
            };
            _entries[key] = terminal;
            return ValueTask.FromResult(terminal);
        }
    }

    private StationControllerCommandJournalEntry ExactEntry(
        CommandKey key,
        string commandSha256)
    {
        if (!_entries.TryGetValue(key, out var current))
        {
            throw new KeyNotFoundException(
                $"Controller command {key.StationId}/{key.CommandId} was not journaled.");
        }

        return string.Equals(
            current.CommandSha256,
            commandSha256,
            StringComparison.Ordinal)
            ? current
            : throw new InvalidDataException(
                $"Controller command {key.CommandId} immutable evidence changed.");
    }

    private static StationControllerCommandAcceptanceResult Acceptance(
        StationControllerCommandAcceptanceStatus status,
        StationControllerCommandJournalEntry? entry,
        long highWater) => new(status, entry, highWater);

    private static CommandKey Key(string stationId, string commandId)
    {
        Required(stationId, nameof(stationId));
        Required(commandId, nameof(commandId));
        return new CommandKey(stationId, commandId);
    }

    private static void ValidateCommand(StationControllerCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Required(command.StationId, nameof(command.StationId));
        Required(command.OwnerAgentId, nameof(command.OwnerAgentId));
        Required(command.OwnerInstanceId, nameof(command.OwnerInstanceId));
        Required(command.CommandId, nameof(command.CommandId));
        if (command.ContractVersion
            != StationControllerCommandFingerprint.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported controller command contract version "
                + $"{command.ContractVersion}.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(command.FencingToken, 1);
    }

    private static void ValidateTerminalStatus(
        StationControllerCommandJournalStatus status)
    {
        if (!IsTerminal(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    private static void ValidateTerminalResult(
        StationControllerCommandJournalStatus status,
        StationPhysicalControllerExecutionResult result)
    {
        var expectedOutcome = status switch
        {
            StationControllerCommandJournalStatus.Completed =>
                StationPhysicalControllerExecutionOutcome.Completed,
            StationControllerCommandJournalStatus.Failed
                or StationControllerCommandJournalStatus.Rejected =>
                StationPhysicalControllerExecutionOutcome.Failed,
            StationControllerCommandJournalStatus.CompletionUnknown =>
                StationPhysicalControllerExecutionOutcome.CompletionUnknown,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
        if (result.Outcome != expectedOutcome)
        {
            throw new ArgumentException(
                $"Journal status {status} requires physical outcome "
                + $"{expectedOutcome}.",
                nameof(result));
        }
    }

    private static bool IsTerminal(StationControllerCommandJournalStatus status) =>
        status is StationControllerCommandJournalStatus.Completed
            or StationControllerCommandJournalStatus.Failed
            or StationControllerCommandJournalStatus.CompletionUnknown
            or StationControllerCommandJournalStatus.Rejected;

    private static void ValidateSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Command SHA-256 must be lowercase hexadecimal.",
                nameof(value));
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

    private sealed record CommandKey(string StationId, string CommandId);

    private sealed record FenceOwner(
        long Token,
        string AgentId,
        string OwnerInstanceId)
    {
        public bool IsOwnedBy(StationControllerCommandEnvelope command) =>
            string.Equals(AgentId, command.OwnerAgentId, StringComparison.Ordinal)
            && string.Equals(
                OwnerInstanceId,
                command.OwnerInstanceId,
                StringComparison.Ordinal);
    }
}
