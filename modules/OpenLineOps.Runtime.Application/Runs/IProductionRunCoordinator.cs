using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public interface IProductionRunCoordinator
{
    ValueTask<Result<ProductionRunSnapshot>> SubmitAsync(
        SubmitProductionRunRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<Result<ProductionRunSnapshot>> CommandAsync(
        ProductionRunId runId,
        ProductionRunCommandRequest command,
        CancellationToken cancellationToken = default);
}

public enum ProductionRunCommand
{
    Pause = 1,
    Continue = 2,
    Stop = 3,
    Hold = 4,
    Release = 5,
    Rework = 6,
    Scrap = 7,
    SafeStop = 8,
    Reconcile = 9,
    Retry = 10,
    Abort = 11
}

public sealed record ProductionRunCommandRequest
{
    public ProductionRunCommandRequest(
        ProductionRunCommand command,
        string actorId,
        string? reason = null,
        string? operationId = null)
    {
        if (!Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported run command.");
        }

        Command = command;
        ActorId = Required(actorId, nameof(actorId));
        Reason = reason is null ? null : Required(reason, nameof(reason));
        OperationId = operationId is null ? null : Required(operationId, nameof(operationId));
        if (command is ProductionRunCommand.Rework or ProductionRunCommand.Retry
            && OperationId is null)
        {
            throw new ArgumentException(
                $"{command} requires an operation id.",
                nameof(operationId));
        }
    }

    public ProductionRunCommand Command { get; }

    public string ActorId { get; }

    public string? Reason { get; }

    public string? OperationId { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;
}
