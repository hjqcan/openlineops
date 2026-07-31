namespace OpenLineOps.Agent.Application.StationController;

public enum StationControllerCommandJournalStatus
{
    Accepted = 1,
    Invoking = 2,
    Completed = 3,
    Failed = 4,
    CompletionUnknown = 5,
    Rejected = 6
}

public sealed record StationControllerCommandJournalEntry(
    StationControllerCommandEnvelope Command,
    string CommandSha256,
    StationControllerCommandJournalStatus Status,
    int InvocationAttemptCount,
    DateTimeOffset AcceptedAtUtc,
    DateTimeOffset? InvocationStartedAtUtc,
    DateTimeOffset? TerminalAtUtc,
    StationPhysicalControllerExecutionResult? Result);

public enum StationControllerCommandAcceptanceStatus
{
    Accepted = 1,
    Existing = 2,
    StaleFencingToken = 3,
    FencingOwnerMismatch = 4,
    CommandIdentityConflict = 5
}

public sealed record StationControllerCommandAcceptanceResult(
    StationControllerCommandAcceptanceStatus Status,
    StationControllerCommandJournalEntry? Entry,
    long FencingTokenHighWater);

public sealed record StationControllerCommandInvocationClaim(
    bool Claimed,
    StationControllerCommandJournalEntry Entry);

public interface IStationControllerCommandJournal
{
    ValueTask<StationControllerCommandAcceptanceResult> TryAcceptAsync(
        StationControllerCommandEnvelope command,
        string commandSha256,
        DateTimeOffset acceptedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<StationControllerCommandJournalEntry?> GetAsync(
        string stationId,
        string commandId,
        CancellationToken cancellationToken = default);

    ValueTask<long?> GetFencingTokenHighWaterAsync(
        string stationId,
        CancellationToken cancellationToken = default);

    ValueTask<StationControllerCommandInvocationClaim> MarkInvokingAsync(
        string stationId,
        string commandId,
        string commandSha256,
        StationControllerCommandJournalStatus expectedStatus,
        int expectedInvocationAttemptCount,
        DateTimeOffset invocationStartedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<StationControllerCommandJournalEntry> CompleteAsync(
        string stationId,
        string commandId,
        string commandSha256,
        StationControllerCommandJournalStatus terminalStatus,
        StationPhysicalControllerExecutionResult result,
        DateTimeOffset terminalAtUtc,
        CancellationToken cancellationToken = default);
}
