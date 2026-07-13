using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class ProductionRunSnapshotMapper
{
    internal const int CurrentSchemaVersion = 1;
    internal const string CurrentResourceKind = "OpenLineOps.ProductionRun";

    public static PersistedProductionRun ToSnapshot(ProductionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var snapshot = run.ToSnapshot();
        return new PersistedProductionRun(
            CurrentSchemaVersion,
            CurrentResourceKind,
            snapshot.RunId.Value,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.ProjectSnapshotId,
            snapshot.TopologyId,
            snapshot.ProductionLineDefinitionId,
            snapshot.ProductionUnitId.Value,
            snapshot.ProductionUnitIdentity.ModelId,
            snapshot.ProductionUnitIdentity.InputKey,
            snapshot.ProductionUnitIdentity.Value,
            snapshot.LotId,
            snapshot.CarrierId,
            snapshot.ActorId,
            snapshot.ExecutionStatus.ToString(),
            snapshot.Judgement.ToString(),
            snapshot.Disposition.ToString(),
            snapshot.ControlState.ToString(),
            snapshot.SafeStopRequestedBy,
            snapshot.SafeStopReason,
            snapshot.SafeStopRequestedAtUtc,
            snapshot.SafeStopAcknowledgedAtUtc,
            snapshot.CreatedAtUtc,
            snapshot.LastTransitionAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.FailureCode,
            snapshot.FailureReason,
            snapshot.EntryOperationId,
            snapshot.OperationDefinitions.Select(ToSnapshot).ToArray(),
            snapshot.RouteTransitions.Select(ToSnapshot).ToArray(),
            snapshot.Operations.Select(ToSnapshot).ToArray(),
            snapshot.RouteDecisions.Select(decision => new PersistedRouteDecision(
                decision.SourceOperationRunId,
                decision.TransitionId,
                decision.TargetOperationId,
                decision.TerminalDisposition?.ToString(),
                decision.SourceJudgement.ToString(),
                decision.Traversal,
                decision.DecidedAtUtc)).ToArray(),
            snapshot.TransitionTraversals.Select(pair =>
                new PersistedTransitionTraversal(pair.Key, pair.Value)).ToArray(),
            snapshot.RecoveryDecisions.Select(ToSnapshot).ToArray());
    }

    public static ProductionRun ToAggregate(PersistedProductionRun snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireCurrentSchema(snapshot);
        if (snapshot.RunId == Guid.Empty)
        {
            throw new InvalidDataException("Persisted Production Run does not declare a run id.");
        }

        if (snapshot.ProductionUnitId == Guid.Empty)
        {
            throw new InvalidDataException("Persisted Production Run does not declare a Production Unit id.");
        }

        var definitions = Required(snapshot.OperationDefinitions, "operation definitions")
            .Select(ToAggregate).ToArray();
        var definitionById = definitions.ToDictionary(
            static definition => definition.OperationId,
            StringComparer.Ordinal);
        var transitions = Required(snapshot.RouteTransitions, "route transitions")
            .Select(ToAggregate).ToArray();
        var operations = Required(snapshot.Operations, "operation runs")
            .Select(operation => ToAggregate(operation, definitionById)).ToArray();
        var decisions = Required(snapshot.RouteDecisions, "route decisions")
            .Select(ToAggregate).ToArray();
        var traversalItems = Required(snapshot.TransitionTraversals, "transition traversals");
        var traversalCounts = traversalItems.ToDictionary(
            item => Text(item.TransitionId, "transition traversal id"),
            item => Positive(item.Count, "transition traversal count"),
            StringComparer.Ordinal);
        if (traversalCounts.Count != traversalItems.Length)
        {
            throw new InvalidDataException("Persisted transition traversal ids must be unique.");
        }

        var recoveryDecisions = Required(snapshot.RecoveryDecisions, "recovery decisions")
            .Select(ToAggregate)
            .ToArray();

        return ProductionRun.Restore(new ProductionRunSnapshot(
            new ProductionRunId(snapshot.RunId),
            Text(snapshot.ProjectId, "project id"),
            Text(snapshot.ApplicationId, "application id"),
            Text(snapshot.ProjectSnapshotId, "project snapshot id"),
            Text(snapshot.TopologyId, "topology id"),
            Text(snapshot.ProductionLineDefinitionId, "production line definition id"),
            new ProductionUnitId(snapshot.ProductionUnitId),
            new ProductionUnitIdentity(
                Text(snapshot.ProductModelId, "product model id"),
                Text(snapshot.IdentityInputKey, "product identity input key"),
                Text(snapshot.IdentityValue, "product identity value")),
            Optional(snapshot.LotId, "lot id"),
            Optional(snapshot.CarrierId, "carrier id"),
            Text(snapshot.ActorId, "actor id"),
            Enum<ExecutionStatus>(snapshot.ExecutionStatus, "execution status"),
            Enum<ResultJudgement>(snapshot.Judgement, "result judgement"),
            Enum<ProductDisposition>(snapshot.Disposition, "product disposition"),
            Enum<ProductionRunControlState>(snapshot.ControlState, "control state"),
            Optional(snapshot.SafeStopRequestedBy, "Safe Stop actor"),
            Optional(snapshot.SafeStopReason, "Safe Stop reason"),
            snapshot.SafeStopRequestedAtUtc,
            snapshot.SafeStopAcknowledgedAtUtc,
            snapshot.CreatedAtUtc,
            snapshot.LastTransitionAtUtc,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            Optional(snapshot.FailureCode, "failure code"),
            Optional(snapshot.FailureReason, "failure reason"),
            Text(snapshot.EntryOperationId, "entry operation id"),
            definitions,
            transitions,
            operations,
            decisions,
            traversalCounts,
            recoveryDecisions));
    }

    private static PersistedOperationDefinition ToSnapshot(OperationRunDefinition definition) => new(
        definition.OperationId,
        definition.StationSystemId,
        definition.StationId.Value,
        definition.ProcessDefinitionId.Value,
        definition.ProcessVersionId.Value,
        definition.ConfigurationSnapshotId.Value,
        definition.RecipeSnapshotId.Value,
        definition.ResourceRequirements.Select(requirement =>
            new PersistedResourceRequirement(requirement.Kind.ToString(), requirement.ResourceId)).ToArray(),
        ToSnapshot(definition.MaterialSlotRequirement));

    private static OperationRunDefinition ToAggregate(PersistedOperationDefinition definition) => new(
        Text(definition.OperationId, "operation id"),
        Text(definition.StationSystemId, "station system id"),
        new StationId(Text(definition.StationId, "station id")),
        new ProcessDefinitionId(Text(definition.ProcessDefinitionId, "process definition id")),
        new ProcessVersionId(Text(definition.ProcessVersionId, "process version id")),
        new ConfigurationSnapshotId(Text(
            definition.ConfigurationSnapshotId,
            "configuration snapshot id")),
        new RecipeSnapshotId(Text(definition.RecipeSnapshotId, "recipe snapshot id")),
        Required(definition.Resources, "operation resources").Select(ToAggregate),
        ToAggregate(definition.MaterialSlotRequirement));

    private static PersistedMaterialSlotRequirement? ToSnapshot(
        MaterialSlotRequirement? requirement) => requirement is null
        ? null
        : new PersistedMaterialSlotRequirement(
            requirement.Resolution.ToString(),
            requirement.TopologyTargetId,
            requirement.EligibleSlotIds.ToArray());

    private static MaterialSlotRequirement? ToAggregate(
        PersistedMaterialSlotRequirement? requirement) => requirement is null
        ? null
        : new MaterialSlotRequirement(
            Enum<MaterialSlotResolution>(requirement.Resolution, "material Slot resolution"),
            Text(requirement.TopologyTargetId, "material Slot topology target id"),
            Required(requirement.EligibleSlotIds, "eligible material Slot ids"));

    private static ResourceRequirement ToAggregate(PersistedResourceRequirement resource) => new(
        Enum<ResourceKind>(resource.Kind, "resource kind"),
        Text(resource.ResourceId, "resource id"));

    private static PersistedRouteTransition ToSnapshot(RouteTransitionDefinition transition) => new(
        transition.TransitionId,
        transition.SourceOperationId,
        transition.TargetOperationId,
        transition.TerminalDisposition?.ToString(),
        transition.Kind.ToString(),
        transition.RequiredJudgement?.ToString(),
        transition.MaxTraversals,
        transition.ParallelGroupId,
        transition.OutputCondition?.OutputKey,
        transition.OutputCondition?.ExpectedValue.Kind.ToString(),
        transition.OutputCondition?.ExpectedValue.CanonicalValue);

    private static RouteTransitionDefinition ToAggregate(PersistedRouteTransition transition) => new(
        Text(transition.TransitionId, "transition id"),
        Text(transition.SourceOperationId, "source operation id"),
        Optional(transition.TargetOperationId, "target operation id"),
        Enum<RuntimeRouteTransitionKind>(transition.Kind, "transition kind"),
        transition.RequiredJudgement is null
            ? null
            : Enum<ResultJudgement>(transition.RequiredJudgement, "required judgement"),
        transition.MaxTraversals,
        Optional(transition.ParallelGroupId, "parallel group id"),
        transition.OutputKey is null
            && transition.ExpectedOutputKind is null
            && transition.ExpectedOutputValue is null
                ? null
                : new RouteOutputCondition(
                    Text(transition.OutputKey, "route output key"),
                    new ProductionContextValue(
                        Enum<ProductionContextValueKind>(
                            transition.ExpectedOutputKind,
                            "expected output kind"),
                        Text(transition.ExpectedOutputValue, "expected output value"))),
        transition.TerminalDisposition is null
            ? null
            : Enum<ProductDisposition>(transition.TerminalDisposition, "terminal disposition"));

    private static RouteDecisionSnapshot ToAggregate(PersistedRouteDecision decision)
    {
        var targetOperationId = Optional(decision.TargetOperationId, "route target operation id");
        ProductDisposition? terminalDisposition = decision.TerminalDisposition is null
            ? null
            : Enum<ProductDisposition>(decision.TerminalDisposition, "route terminal disposition");
        if ((targetOperationId is null) == (terminalDisposition is null))
        {
            throw new InvalidDataException(
                "Persisted route decision requires exactly one target Operation or terminal disposition.");
        }

        return new RouteDecisionSnapshot(
            Text(decision.SourceOperationRunId, "route source Operation Run id"),
            Text(decision.TransitionId, "route transition id"),
            targetOperationId,
            terminalDisposition,
            Enum<ResultJudgement>(decision.SourceJudgement, "route source judgement"),
            Positive(decision.Traversal, "route traversal"),
            decision.DecidedAtUtc);
    }

    private static PersistedOperationRun ToSnapshot(OperationRunSnapshot operation) => new(
        operation.Definition.OperationId,
        operation.OperationRunId,
        operation.Attempt,
        operation.ExecutionStatus.ToString(),
        operation.Judgement.ToString(),
        operation.RuntimeSessionId?.Value,
        operation.StartedAtUtc,
        operation.CompletedAtUtc,
        operation.FailureCode,
        operation.FailureReason,
        operation.CompletedStepCount,
        operation.CommandCount,
        operation.IncidentCount,
        operation.Outputs.Select(pair => new PersistedProductionContextValue(
            pair.Key,
            pair.Value.Kind.ToString(),
            pair.Value.CanonicalValue)).ToArray(),
        operation.FencingTokens.Select(pair => new PersistedFencingToken(
            pair.Key.Kind.ToString(),
            pair.Key.ResourceId,
            pair.Value)).ToArray());

    private static PersistedRecoveryDecision ToSnapshot(ProductionRecoveryDecision decision) => new(
        decision.DecisionId,
        decision.Kind.ToString(),
        decision.ActorId,
        decision.Reason,
        decision.EvidenceReference,
        decision.DecidedAtUtc,
        decision.OperationRunId,
        decision.OperationId,
        decision.ObservedJudgement?.ToString(),
        decision.ObservedOutputs
            .OrderBy(output => output.Key, StringComparer.Ordinal)
            .Select(output => new PersistedProductionContextValue(
                output.Key,
                output.Value.Kind.ToString(),
                output.Value.CanonicalValue))
            .ToArray());

    private static ProductionRecoveryDecision ToAggregate(PersistedRecoveryDecision decision)
    {
        if (decision.DecisionId == Guid.Empty)
        {
            throw new InvalidDataException("Persisted Recovery Decision does not declare an id.");
        }

        var outputItems = Required(decision.ObservedOutputs, "Recovery Decision observed outputs");
        var outputs = outputItems.ToDictionary(
            item => Text(item.Key, "Recovery Decision output key"),
            item => new ProductionContextValue(
                Enum<ProductionContextValueKind>(item.Kind, "Recovery Decision output kind"),
                Text(item.Value, "Recovery Decision output value")),
            StringComparer.Ordinal);
        if (outputs.Count != outputItems.Length)
        {
            throw new InvalidDataException("Persisted Recovery Decision output keys must be unique.");
        }

        return new ProductionRecoveryDecision(
            decision.DecisionId,
            Enum<ProductionRecoveryDecisionKind>(decision.Kind, "Recovery Decision kind"),
            Text(decision.ActorId, "Recovery Decision actor"),
            Text(decision.Reason, "Recovery Decision reason"),
            Text(decision.EvidenceReference, "Recovery Decision evidence reference"),
            decision.DecidedAtUtc,
            Optional(decision.OperationRunId, "Recovery Decision Operation Run id"),
            Optional(decision.OperationId, "Recovery Decision Operation id"),
            decision.ObservedJudgement is null
                ? null
                : Enum<ResultJudgement>(decision.ObservedJudgement, "Recovery Decision judgement"),
            outputs);
    }

    private static OperationRunSnapshot ToAggregate(
        PersistedOperationRun operation,
        Dictionary<string, OperationRunDefinition> definitions)
    {
        var operationId = Text(operation.OperationId, "Operation Run operation id");
        if (!definitions.TryGetValue(operationId, out var definition))
        {
            throw new InvalidDataException(
                $"Persisted Operation Run references unknown operation {operationId}.");
        }

        var outputItems = Required(operation.Outputs, "operation outputs");
        var outputs = outputItems.ToDictionary(
            item => Text(item.Key, "operation output key"),
            item => new ProductionContextValue(
                Enum<ProductionContextValueKind>(item.Kind, "operation output kind"),
                Text(item.Value, "operation output value")),
            StringComparer.Ordinal);
        if (outputs.Count != outputItems.Length)
        {
            throw new InvalidDataException("Persisted operation output keys must be unique.");
        }

        var fencingItems = Required(operation.FencingTokens, "operation fencing tokens");
        var fencingTokens = fencingItems.ToDictionary(
            item => new ResourceRequirement(
                Enum<ResourceKind>(item.ResourceKind, "fencing resource kind"),
                Text(item.ResourceId, "fencing resource id")),
            item => Positive(item.FencingToken, "fencing token"));
        if (fencingTokens.Count != fencingItems.Length)
        {
            throw new InvalidDataException("Persisted operation fencing resources must be unique.");
        }

        return new OperationRunSnapshot(
            definition,
            Text(operation.OperationRunId, "Operation Run id"),
            Positive(operation.Attempt, "operation attempt"),
            Enum<ExecutionStatus>(operation.ExecutionStatus, "operation execution status"),
            Enum<ResultJudgement>(operation.Judgement, "operation judgement"),
            operation.RuntimeSessionId is null
                ? null
                : new RuntimeSessionId(operation.RuntimeSessionId.Value),
            operation.StartedAtUtc,
            operation.CompletedAtUtc,
            Optional(operation.FailureCode, "operation failure code"),
            Optional(operation.FailureReason, "operation failure reason"),
            operation.CompletedStepCount,
            operation.CommandCount,
            operation.IncidentCount,
            outputs,
            fencingTokens);
    }

    private static void RequireCurrentSchema(PersistedProductionRun snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(snapshot.ResourceKind, CurrentResourceKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted Production Run schema must be exactly {CurrentResourceKind} "
                + $"schema {CurrentSchemaVersion}.");
        }
    }

    private static T[] Required<T>(T[]? values, string fieldName) =>
        values ?? throw new InvalidDataException(
            $"Persisted Production Run does not declare {fieldName}.");

    private static string Text(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"Persisted Production Run does not declare canonical {fieldName}.")
            : value;

    private static string? Optional(string? value, string fieldName) =>
        value is null ? null : Text(value, fieldName);

    private static int Positive(int value, string fieldName) =>
        value > 0
            ? value
            : throw new InvalidDataException(
                $"Persisted Production Run {fieldName} must be positive.");

    private static long Positive(long value, string fieldName) =>
        value > 0
            ? value
            : throw new InvalidDataException(
                $"Persisted Production Run {fieldName} must be positive.");

    private static TEnum Enum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (value is not null && CanonicalEnumToken.TryParse<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidDataException(
            $"Persisted Production Run {fieldName} value '{value}' is invalid. Expected "
            + $"{CanonicalEnumToken.ExpectedTokens<TEnum>()}.");
    }
}

internal sealed record PersistedProductionRun(
    int SchemaVersion,
    string? ResourceKind,
    Guid RunId,
    string? ProjectId,
    string? ApplicationId,
    string? ProjectSnapshotId,
    string? TopologyId,
    string? ProductionLineDefinitionId,
    Guid ProductionUnitId,
    string? ProductModelId,
    string? IdentityInputKey,
    string? IdentityValue,
    string? LotId,
    string? CarrierId,
    string? ActorId,
    string? ExecutionStatus,
    string? Judgement,
    string? Disposition,
    string? ControlState,
    string? SafeStopRequestedBy,
    string? SafeStopReason,
    DateTimeOffset? SafeStopRequestedAtUtc,
    DateTimeOffset? SafeStopAcknowledgedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastTransitionAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    string? EntryOperationId,
    PersistedOperationDefinition[]? OperationDefinitions,
    PersistedRouteTransition[]? RouteTransitions,
    PersistedOperationRun[]? Operations,
    PersistedRouteDecision[]? RouteDecisions,
    PersistedTransitionTraversal[]? TransitionTraversals,
    PersistedRecoveryDecision[]? RecoveryDecisions);

internal sealed record PersistedOperationDefinition(
    string? OperationId,
    string? StationSystemId,
    string? StationId,
    string? ProcessDefinitionId,
    string? ProcessVersionId,
    string? ConfigurationSnapshotId,
    string? RecipeSnapshotId,
    PersistedResourceRequirement[]? Resources,
    PersistedMaterialSlotRequirement? MaterialSlotRequirement);

internal sealed record PersistedResourceRequirement(string? Kind, string? ResourceId);

internal sealed record PersistedMaterialSlotRequirement(
    string? Resolution,
    string? TopologyTargetId,
    string[]? EligibleSlotIds);

internal sealed record PersistedRouteTransition(
    string? TransitionId,
    string? SourceOperationId,
    string? TargetOperationId,
    string? TerminalDisposition,
    string? Kind,
    string? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    string? ExpectedOutputKind,
    string? ExpectedOutputValue);

internal sealed record PersistedOperationRun(
    string? OperationId,
    string? OperationRunId,
    int Attempt,
    string? ExecutionStatus,
    string? Judgement,
    Guid? RuntimeSessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    PersistedProductionContextValue[]? Outputs,
    PersistedFencingToken[]? FencingTokens);

internal sealed record PersistedProductionContextValue(string? Key, string? Kind, string? Value);

internal sealed record PersistedFencingToken(
    string? ResourceKind,
    string? ResourceId,
    long FencingToken);

internal sealed record PersistedRouteDecision(
    string? SourceOperationRunId,
    string? TransitionId,
    string? TargetOperationId,
    string? TerminalDisposition,
    string? SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

internal sealed record PersistedTransitionTraversal(string? TransitionId, int Count);

internal sealed record PersistedRecoveryDecision(
    Guid DecisionId,
    string? Kind,
    string? ActorId,
    string? Reason,
    string? EvidenceReference,
    DateTimeOffset DecidedAtUtc,
    string? OperationRunId,
    string? OperationId,
    string? ObservedJudgement,
    PersistedProductionContextValue[]? ObservedOutputs);
