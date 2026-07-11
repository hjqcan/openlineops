using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public interface IStationOperationCanceler
{
    ValueTask<StationOperationCancellationResult> CancelAsync(
        StationOperationCancellationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StationOperationCancellationRequest
{
    public StationOperationCancellationRequest(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        string actorId,
        string reason,
        DateTimeOffset requestedAtUtc)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        ActorId = Required(actorId, nameof(actorId));
        Reason = Required(reason, nameof(reason));
        RequestedAtUtc = requestedAtUtc.Offset == TimeSpan.Zero
            ? requestedAtUtc
            : throw new ArgumentException(
                "Station operation cancellation timestamp must use UTC offset zero.",
                nameof(requestedAtUtc));
        if (!run.Operations.Any(candidate => string.Equals(
                candidate.OperationRunId,
                operation.OperationRunId,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Station operation cancellation target does not belong to the Production Run.",
                nameof(operation));
        }
    }

    public ProductionRunSnapshot Run { get; }

    public OperationRunSnapshot Operation { get; }

    public string ActorId { get; }

    public string Reason { get; }

    public DateTimeOffset RequestedAtUtc { get; }

    public string JobIdempotencyKey => $"{Run.RunId.Value:D}/{Operation.OperationRunId}";

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;
}

public sealed record StationOperationCancellationResult(
    bool Accepted,
    string? FailureCode,
    string? FailureReason)
{
    public static StationOperationCancellationResult Success() => new(true, null, null);

    public static StationOperationCancellationResult Failure(string code, string reason) =>
        new(false, code, reason);
}
