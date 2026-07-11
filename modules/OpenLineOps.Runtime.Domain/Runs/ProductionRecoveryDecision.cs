using System.Collections.ObjectModel;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Domain.Runs;

public enum ProductionRecoveryDecisionKind
{
    Reconcile = 1,
    Retry = 2,
    Abort = 3,
    Scrap = 4
}

public sealed record ProductionRecoveryDecision
{
    public ProductionRecoveryDecision(
        Guid decisionId,
        ProductionRecoveryDecisionKind kind,
        string actorId,
        string reason,
        string evidenceReference,
        DateTimeOffset decidedAtUtc,
        string? operationRunId = null,
        string? operationId = null,
        ResultJudgement? observedJudgement = null,
        IReadOnlyDictionary<string, ProductionContextValue>? observedOutputs = null)
    {
        if (decisionId == Guid.Empty)
        {
            throw new ArgumentException("Recovery Decision id cannot be empty.", nameof(decisionId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported recovery decision kind.");
        }

        if (decidedAtUtc == default || decidedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recovery Decision timestamp must be UTC.", nameof(decidedAtUtc));
        }

        DecisionId = decisionId;
        Kind = kind;
        ActorId = ProductionRunText.Required(actorId, nameof(actorId));
        Reason = ProductionRunText.Required(reason, nameof(reason));
        EvidenceReference = ProductionRunText.Required(evidenceReference, nameof(evidenceReference));
        DecidedAtUtc = decidedAtUtc;
        OperationRunId = ProductionRunText.Optional(operationRunId, nameof(operationRunId));
        OperationId = ProductionRunText.Optional(operationId, nameof(operationId));
        if (observedJudgement is not null && !Enum.IsDefined(observedJudgement.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedJudgement),
                observedJudgement,
                "Unsupported observed judgement.");
        }

        ObservedJudgement = observedJudgement;
        ObservedOutputs = NormalizeOutputs(observedOutputs);
        ValidateShape();
    }

    public Guid DecisionId { get; }

    public ProductionRecoveryDecisionKind Kind { get; }

    public string ActorId { get; }

    public string Reason { get; }

    public string EvidenceReference { get; }

    public DateTimeOffset DecidedAtUtc { get; }

    public string? OperationRunId { get; }

    public string? OperationId { get; }

    public ResultJudgement? ObservedJudgement { get; }

    public IReadOnlyDictionary<string, ProductionContextValue> ObservedOutputs { get; }

    private void ValidateShape()
    {
        switch (Kind)
        {
            case ProductionRecoveryDecisionKind.Reconcile:
                if (OperationRunId is null
                    || OperationId is not null
                    || ObservedJudgement is not (ResultJudgement.Passed
                        or ResultJudgement.Failed
                        or ResultJudgement.NotApplicable))
                {
                    throw new ArgumentException(
                        "Reconcile requires one Operation Run id and an observed Passed, Failed, or NotApplicable judgement.");
                }

                break;
            case ProductionRecoveryDecisionKind.Retry:
                if (OperationRunId is not null
                    || OperationId is null
                    || ObservedJudgement is not null
                    || ObservedOutputs.Count != 0)
                {
                    throw new ArgumentException(
                        "Retry requires one target Operation id and cannot claim observed output or judgement.");
                }

                break;
            case ProductionRecoveryDecisionKind.Abort:
            case ProductionRecoveryDecisionKind.Scrap:
                if (OperationRunId is not null
                    || OperationId is not null
                    || ObservedJudgement is not null
                    || ObservedOutputs.Count != 0)
                {
                    throw new ArgumentException(
                        $"{Kind} is a run-level recovery decision and cannot declare Operation or observed-result fields.");
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported recovery decision kind {Kind}.");
        }
    }

    private static ReadOnlyDictionary<string, ProductionContextValue> NormalizeOutputs(
        IReadOnlyDictionary<string, ProductionContextValue>? outputs)
    {
        var normalized = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        foreach (var output in outputs ?? new Dictionary<string, ProductionContextValue>())
        {
            var key = ProductionRunText.Required(output.Key, "observed output key");
            normalized.Add(
                key,
                output.Value ?? throw new ArgumentException("Observed output cannot be null."));
        }

        return new ReadOnlyDictionary<string, ProductionContextValue>(normalized);
    }
}
