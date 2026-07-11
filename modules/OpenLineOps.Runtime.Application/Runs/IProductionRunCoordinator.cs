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
    Cancel = 11,
    Abort = 12
}

public sealed record ProductionRunCommandRequest
{
    public ProductionRunCommandRequest(
        ProductionRunCommand command,
        string actorId,
        string? reason = null,
        string? operationId = null,
        ProductionRecoveryDecision? recoveryDecision = null)
    {
        if (!Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported run command.");
        }

        Command = command;
        ActorId = Required(actorId, nameof(actorId));
        Reason = reason is null ? null : Required(reason, nameof(reason));
        OperationId = operationId is null ? null : Required(operationId, nameof(operationId));
        RecoveryDecision = recoveryDecision;
        if (recoveryDecision is not null
            && !string.Equals(recoveryDecision.ActorId, ActorId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Recovery Decision actor must equal the command actor.",
                nameof(recoveryDecision));
        }
        if (command == ProductionRunCommand.Rework && OperationId is null)
        {
            throw new ArgumentException(
                $"{command} requires an operation id.",
                nameof(operationId));
        }

        var expectedRecoveryKind = command switch
        {
            ProductionRunCommand.Reconcile => ProductionRecoveryDecisionKind.Reconcile,
            ProductionRunCommand.Retry => ProductionRecoveryDecisionKind.Retry,
            ProductionRunCommand.Abort => ProductionRecoveryDecisionKind.Abort,
            _ => (ProductionRecoveryDecisionKind?)null
        };
        if (expectedRecoveryKind is not null
            && recoveryDecision?.Kind != expectedRecoveryKind)
        {
            throw new ArgumentException(
                $"{command} requires a {expectedRecoveryKind} Recovery Decision.",
                nameof(recoveryDecision));
        }

        if (command == ProductionRunCommand.Scrap
            && recoveryDecision is not null
            && recoveryDecision.Kind != ProductionRecoveryDecisionKind.Scrap)
        {
            throw new ArgumentException(
                "Scrap can only carry a Scrap Recovery Decision.",
                nameof(recoveryDecision));
        }

        if (expectedRecoveryKind is null
            && command != ProductionRunCommand.Scrap
            && recoveryDecision is not null)
        {
            throw new ArgumentException(
                $"{command} cannot carry a Recovery Decision.",
                nameof(recoveryDecision));
        }
    }

    public ProductionRunCommand Command { get; }

    public string ActorId { get; }

    public string? Reason { get; }

    public string? OperationId { get; }

    public ProductionRecoveryDecision? RecoveryDecision { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be a non-empty canonical string.",
                parameterName)
            : value;
}
