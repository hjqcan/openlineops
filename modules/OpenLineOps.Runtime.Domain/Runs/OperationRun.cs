using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed class OperationRun
{
    private const string PreExecutionCancellationFailureCode =
        "Runtime.ProductionRunStopped";
    internal const string ReworkSupersededFailureCode =
        "Runtime.OperationSupersededByRework";
    internal const string ReworkSupersededFailureReason =
        "Pending Operation attempt was superseded by an upstream Rework wave.";

    private readonly Dictionary<string, ProductionContextValue> _outputs;
    private readonly Dictionary<ResourceRequirement, long> _fencingTokens;
    private readonly Dictionary<string, string> _sourceOperationRunBindings;

    private OperationRun(
        OperationRunDefinition definition,
        string operationRunId,
        int attempt,
        ExecutionStatus executionStatus,
        ResultJudgement judgement,
        RuntimeSessionId? runtimeSessionId,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? failureCode,
        string? failureReason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        Guid? recoveryDecisionId,
        OperationExecutionEvidence? executionEvidence,
        IReadOnlyDictionary<string, ProductionContextValue>? outputs,
        IReadOnlyDictionary<ResourceRequirement, long>? fencingTokens,
        IReadOnlyDictionary<string, string> sourceOperationRunBindings)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        OperationRunId = ProductionRunText.Required(operationRunId, nameof(operationRunId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        Attempt = attempt;
        if (!Enum.IsDefined(executionStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(executionStatus),
                executionStatus,
                "Unsupported operation execution status.");
        }

        if (!Enum.IsDefined(judgement))
        {
            throw new ArgumentOutOfRangeException(nameof(judgement), judgement, "Unsupported judgement.");
        }

        ExecutionStatus = executionStatus;
        Judgement = judgement;
        RuntimeSessionId = runtimeSessionId;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        FailureCode = ProductionRunText.Optional(failureCode, nameof(failureCode));
        FailureReason = ProductionRunText.Optional(failureReason, nameof(failureReason));
        CompletedStepCount = completedStepCount;
        CommandCount = commandCount;
        IncidentCount = incidentCount;
        if (recoveryDecisionId == Guid.Empty)
        {
            throw new ArgumentException("Recovery Decision id cannot be empty.", nameof(recoveryDecisionId));
        }

        RecoveryDecisionId = recoveryDecisionId;
        ExecutionEvidence = executionEvidence;
        _outputs = outputs?.ToDictionary() ?? [];
        _fencingTokens = fencingTokens?.ToDictionary() ?? [];
        ArgumentNullException.ThrowIfNull(sourceOperationRunBindings);
        _sourceOperationRunBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (operationId, boundOperationRunId) in sourceOperationRunBindings)
        {
            var canonicalOperationId = ProductionRunText.Required(
                operationId,
                "source Operation id");
            var canonicalOperationRunId = ProductionRunText.Required(
                boundOperationRunId,
                "source Operation Run id");
            if (string.Equals(canonicalOperationId, OperationId, StringComparison.Ordinal)
                || !_sourceOperationRunBindings.TryAdd(
                    canonicalOperationId,
                    canonicalOperationRunId))
            {
                throw new ArgumentException(
                    "Source Operation Run bindings must be unique and cannot bind the current Operation.",
                    nameof(sourceOperationRunBindings));
            }
        }
        ValidateState();
    }

    public string OperationRunId { get; }

    public string OperationId => Definition.OperationId;

    public int Attempt { get; }

    public string StationSystemId => Definition.StationSystemId;

    public StationId StationId => Definition.StationId;

    public ProcessDefinitionId ProcessDefinitionId => Definition.ProcessDefinitionId;

    public ProcessVersionId ProcessVersionId => Definition.ProcessVersionId;

    public ConfigurationSnapshotId ConfigurationSnapshotId => Definition.ConfigurationSnapshotId;

    public RecipeSnapshotId RecipeSnapshotId => Definition.RecipeSnapshotId;

    public IReadOnlyList<ResourceRequirement> ResourceRequirements => Definition.ResourceRequirements;

    public ExecutionStatus ExecutionStatus { get; private set; }

    public ResultJudgement Judgement { get; private set; }

    public RuntimeSessionId? RuntimeSessionId { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureReason { get; private set; }

    public int CompletedStepCount { get; private set; }

    public int CommandCount { get; private set; }

    public int IncidentCount { get; private set; }

    public Guid? RecoveryDecisionId { get; private set; }

    public OperationExecutionEvidence? ExecutionEvidence { get; private set; }

    public IReadOnlyDictionary<string, ProductionContextValue> Outputs => _outputs;

    public IReadOnlyDictionary<ResourceRequirement, long> FencingTokens => _fencingTokens;

    public IReadOnlyDictionary<string, string> SourceOperationRunBindings =>
        _sourceOperationRunBindings;

    public bool IsTerminal => ExecutionStatus is ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;

    private OperationRunDefinition Definition { get; }

    internal static OperationRun Create(
        OperationRunDefinition definition,
        int attempt,
        IReadOnlyDictionary<string, string> sourceOperationRunBindings)
    {
        return new OperationRun(
            definition,
            CreateOperationRunId(definition.OperationId, attempt),
            attempt,
            ExecutionStatus.Pending,
            ResultJudgement.Unknown,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            sourceOperationRunBindings);
    }

    internal static OperationRun Restore(OperationRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new OperationRun(
            snapshot.Definition,
            snapshot.OperationRunId,
            snapshot.Attempt,
            snapshot.ExecutionStatus,
            snapshot.Judgement,
            snapshot.RuntimeSessionId,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.FailureCode,
            snapshot.FailureReason,
            snapshot.CompletedStepCount,
            snapshot.CommandCount,
            snapshot.IncidentCount,
            snapshot.RecoveryDecisionId,
            snapshot.ExecutionEvidence,
            snapshot.Outputs,
            snapshot.FencingTokens,
            snapshot.SourceOperationRunBindings);
    }

    internal RuntimeOperationResult Start(
        RuntimeSessionId runtimeSessionId,
        IReadOnlyCollection<ResourceLease> leases,
        DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(leases);
        if (runtimeSessionId.Value == Guid.Empty)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationSessionIdRequired",
                $"Operation Run {OperationRunId} requires a non-empty Runtime Session id.");
        }

        if (ExecutionStatus != ExecutionStatus.Pending)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationStartRejected",
                $"Operation Run {OperationRunId} cannot start from {ExecutionStatus}.");
        }

        var byResource = leases.ToDictionary(static lease => lease.Resource);
        if (byResource.Count != leases.Count
            || Definition.ResourceRequirements.Any(requirement =>
                !byResource.TryGetValue(requirement, out var lease)
                || lease.FencingToken <= 0))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationResourceLeaseRequired",
                $"Operation Run {OperationRunId} requires a valid fencing token for every resource.");
        }

        _fencingTokens.Clear();
        foreach (var lease in leases)
        {
            _fencingTokens.Add(lease.Resource, lease.FencingToken);
        }

        ExecutionStatus = ExecutionStatus.Running;
        RuntimeSessionId = runtimeSessionId;
        StartedAtUtc = startedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    internal RuntimeOperationResult Complete(
        ResultJudgement judgement,
        OperationExecutionEvidence executionEvidence,
        IReadOnlyDictionary<string, ProductionContextValue>? outputs,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset completedAtUtc)
    {
        return CompleteCore(
            judgement,
            executionEvidence,
            recoveryDecisionId: null,
            outputs,
            completedStepCount,
            commandCount,
            incidentCount,
            completedAtUtc);
    }

    internal RuntimeOperationResult CompleteByReconciliation(
        Guid recoveryDecisionId,
        ResultJudgement judgement,
        IReadOnlyDictionary<string, ProductionContextValue>? outputs,
        DateTimeOffset completedAtUtc) => CompleteCore(
            judgement,
            executionEvidence: null,
            recoveryDecisionId,
            outputs,
            CompletedStepCount,
            CommandCount,
            IncidentCount,
            completedAtUtc);

    private RuntimeOperationResult CompleteCore(
        ResultJudgement judgement,
        OperationExecutionEvidence? executionEvidence,
        Guid? recoveryDecisionId,
        IReadOnlyDictionary<string, ProductionContextValue>? outputs,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset completedAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationCompleteRejected",
                $"Operation Run {OperationRunId} cannot complete from {ExecutionStatus}.");
        }

        if (!Enum.IsDefined(judgement))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationJudgementInvalid",
                $"Operation Run {OperationRunId} received an unsupported result judgement.");
        }

        ValidateMetrics(completedStepCount, commandCount, incidentCount);
        var canonicalOutputs = CanonicalOutputs(outputs);
        if (recoveryDecisionId == Guid.Empty)
        {
            throw new ArgumentException("Recovery Decision id cannot be empty.", nameof(recoveryDecisionId));
        }

        if (recoveryDecisionId is null)
        {
            ArgumentNullException.ThrowIfNull(executionEvidence);
            ValidateExecutionEvidence(
                executionEvidence,
                ExecutionStatus.Completed,
                completedStepCount,
                commandCount,
                incidentCount,
                completedAtUtc);
        }

        SetMetrics(completedStepCount, commandCount, incidentCount);
        RecoveryDecisionId = recoveryDecisionId;
        ExecutionEvidence = executionEvidence;
        _outputs.Clear();
        foreach (var output in canonicalOutputs)
        {
            _outputs.Add(output.Key, output.Value);
        }

        ExecutionStatus = ExecutionStatus.Completed;
        Judgement = judgement;
        CompletedAtUtc = completedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    internal RuntimeOperationResult Fail(
        ExecutionStatus terminalStatus,
        OperationExecutionEvidence executionEvidence,
        string code,
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset completedAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running
            || terminalStatus is not (ExecutionStatus.Failed
                or ExecutionStatus.TimedOut
                or ExecutionStatus.Rejected))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationFailRejected",
                $"Operation Run {OperationRunId} cannot transition from {ExecutionStatus} to {terminalStatus}.");
        }

        ValidateMetrics(completedStepCount, commandCount, incidentCount);
        ArgumentNullException.ThrowIfNull(executionEvidence);
        ValidateExecutionEvidence(
            executionEvidence,
            terminalStatus,
            completedStepCount,
            commandCount,
            incidentCount,
            completedAtUtc);
        var failureCode = ProductionRunText.Required(code, nameof(code));
        var failureReason = ProductionRunText.Required(reason, nameof(reason));
        SetMetrics(completedStepCount, commandCount, incidentCount);
        RecoveryDecisionId = null;
        ExecutionEvidence = executionEvidence;
        ExecutionStatus = terminalStatus;
        Judgement = ResultJudgement.Unknown;
        CompletedAtUtc = completedAtUtc;
        FailureCode = failureCode;
        FailureReason = failureReason;
        return RuntimeOperationResult.Accepted();
    }

    internal void Cancel(string reason, DateTimeOffset canceledAtUtc)
    {
        if (IsTerminal)
        {
            return;
        }

        if (ExecutionStatus != ExecutionStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Started Operation Run {OperationRunId} requires execution or Recovery Decision evidence before cancellation.");
        }

        CancelCore(
            PreExecutionCancellationFailureCode,
            reason,
            canceledAtUtc,
            recoveryDecisionId: null);
    }

    internal void CancelSupersededByRework(DateTimeOffset canceledAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Only a Pending Operation Run can be superseded by Rework; {OperationRunId} is {ExecutionStatus}.");
        }

        CancelCore(
            ReworkSupersededFailureCode,
            ReworkSupersededFailureReason,
            canceledAtUtc,
            recoveryDecisionId: null);
    }

    internal void CancelByRecovery(
        Guid recoveryDecisionId,
        string reason,
        DateTimeOffset canceledAtUtc)
    {
        if (IsTerminal)
        {
            return;
        }

        if (recoveryDecisionId == Guid.Empty)
        {
            throw new ArgumentException("Recovery Decision id cannot be empty.", nameof(recoveryDecisionId));
        }

        CancelCore(
            PreExecutionCancellationFailureCode,
            reason,
            canceledAtUtc,
            recoveryDecisionId);
    }

    private void CancelCore(
        string failureCode,
        string reason,
        DateTimeOffset canceledAtUtc,
        Guid? recoveryDecisionId)
    {
        RecoveryDecisionId = recoveryDecisionId;

        ExecutionStatus = ExecutionStatus.Canceled;
        Judgement = ResultJudgement.Aborted;
        CompletedAtUtc = canceledAtUtc;
        FailureCode = ProductionRunText.Required(failureCode, nameof(failureCode));
        FailureReason = ProductionRunText.Required(reason, nameof(reason));
    }

    internal RuntimeOperationResult CancelAfterExecution(
        OperationExecutionEvidence executionEvidence,
        string code,
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset canceledAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationCancelRejected",
                $"Operation Run {OperationRunId} cannot capture execution cancellation from {ExecutionStatus}.");
        }

        if (completedStepCount < 0 || commandCount < 0 || incidentCount < 0)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationCancelMetricsInvalid",
                $"Operation Run {OperationRunId} cancellation metrics cannot be negative.");
        }

        if (StartedAtUtc is null || canceledAtUtc < StartedAtUtc.Value)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationCancelTimestampInvalid",
                $"Operation Run {OperationRunId} cancellation cannot precede its start timestamp.");
        }

        var failureCode = ProductionRunText.Required(code, nameof(code));
        var failureReason = ProductionRunText.Required(reason, nameof(reason));
        ArgumentNullException.ThrowIfNull(executionEvidence);
        ValidateExecutionEvidence(
            executionEvidence,
            ExecutionStatus.Canceled,
            completedStepCount,
            commandCount,
            incidentCount,
            canceledAtUtc);
        SetMetrics(completedStepCount, commandCount, incidentCount);
        RecoveryDecisionId = null;
        ExecutionEvidence = executionEvidence;
        CancelCore(
            failureCode,
            failureReason,
            canceledAtUtc,
            recoveryDecisionId: null);
        ExecutionEvidence = executionEvidence;
        return RuntimeOperationResult.Accepted();
    }

    internal OperationRunSnapshot ToSnapshot() => new(
        Definition,
        OperationRunId,
        Attempt,
        ExecutionStatus,
        Judgement,
        RuntimeSessionId,
        StartedAtUtc,
        CompletedAtUtc,
        FailureCode,
        FailureReason,
        CompletedStepCount,
        CommandCount,
        IncidentCount,
        RecoveryDecisionId,
        ExecutionEvidence,
        new Dictionary<string, ProductionContextValue>(_outputs, StringComparer.Ordinal),
        new Dictionary<ResourceRequirement, long>(_fencingTokens),
        new Dictionary<string, string>(_sourceOperationRunBindings, StringComparer.Ordinal));

    private static string CreateOperationRunId(string operationId, int attempt) =>
        $"{operationId}@{attempt:D4}";

    private void SetMetrics(int completedStepCount, int commandCount, int incidentCount)
    {
        ValidateMetrics(completedStepCount, commandCount, incidentCount);
        CompletedStepCount = completedStepCount;
        CommandCount = commandCount;
        IncidentCount = incidentCount;
    }

    private static void ValidateMetrics(int completedStepCount, int commandCount, int incidentCount)
    {
        if (completedStepCount < 0 || commandCount < 0 || incidentCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedStepCount),
                "Operation execution metrics cannot be negative.");
        }
    }

    private static Dictionary<string, ProductionContextValue> CanonicalOutputs(
        IReadOnlyDictionary<string, ProductionContextValue>? outputs)
    {
        var canonical = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        foreach (var output in outputs ?? new Dictionary<string, ProductionContextValue>())
        {
            var key = ProductionRunText.Required(output.Key, "output key");
            canonical.Add(
                key,
                output.Value ?? throw new ArgumentException("Operation output cannot be null."));
        }

        return canonical;
    }

    private void ValidateExecutionEvidence(
        OperationExecutionEvidence evidence,
        ExecutionStatus terminalStatus,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset completedAtUtc)
    {
        var expectedStatus = terminalStatus switch
        {
            ExecutionStatus.Completed => "Completed",
            ExecutionStatus.Canceled => evidence.RuntimeSessionStatus is "Canceled" or "Stopped"
                ? evidence.RuntimeSessionStatus
                : null,
            ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Rejected => "Failed",
            _ => null
        };
        var expectedFences = _fencingTokens
            .OrderBy(
                static pair => $"{pair.Key.Kind}:{pair.Key.ResourceId}",
                StringComparer.Ordinal)
            .Select(static pair => (pair.Key.Kind.ToString(), pair.Key.ResourceId, pair.Value))
            .ToArray();
        var evidenceFences = evidence.ResourceFences
            .OrderBy(static fence => $"{fence.ResourceKind}:{fence.ResourceId}", StringComparer.Ordinal)
            .Select(static fence => (fence.ResourceKind, fence.ResourceId, fence.FencingToken))
            .ToArray();
        if (RuntimeSessionId?.Value != evidence.RuntimeSessionId
            || !string.Equals(OperationRunId, evidence.OperationRunId, StringComparison.Ordinal)
            || !string.Equals(OperationId, evidence.OperationId, StringComparison.Ordinal)
            || Attempt != evidence.OperationAttempt
            || !string.Equals(StationSystemId, evidence.StationSystemId, StringComparison.Ordinal)
            || !string.Equals(StationId.Value, evidence.StationId, StringComparison.Ordinal)
            || !string.Equals(ProcessDefinitionId.Value, evidence.ProcessDefinitionId, StringComparison.Ordinal)
            || !string.Equals(ProcessVersionId.Value, evidence.ProcessVersionId, StringComparison.Ordinal)
            || !string.Equals(ConfigurationSnapshotId.Value, evidence.ConfigurationSnapshotId, StringComparison.Ordinal)
            || !string.Equals(RecipeSnapshotId.Value, evidence.RecipeSnapshotId, StringComparison.Ordinal)
            || completedAtUtc != evidence.CompletedAtUtc
            || completedStepCount != evidence.Steps.Count(step =>
                string.Equals(step.Status, "Completed", StringComparison.Ordinal))
            || commandCount != evidence.Commands.Count
            || incidentCount != evidence.Incidents.Count
            || !string.Equals(expectedStatus, evidence.RuntimeSessionStatus, StringComparison.Ordinal)
            || !expectedFences.SequenceEqual(evidenceFences)
            || !string.Equals(
                evidence.FixtureId,
                FindResource(ResourceKind.Fixture),
                StringComparison.Ordinal)
            || !string.Equals(
                evidence.DeviceId,
                FindResource(ResourceKind.Device),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Operation Run {OperationRunId} execution evidence does not exactly match its terminal result.",
                nameof(evidence));
        }
    }

    private string? FindResource(ResourceKind kind) => Definition.ResourceRequirements
        .FirstOrDefault(requirement => requirement.Kind == kind)?.ResourceId;

    private void ValidateState()
    {
        SetMetrics(CompletedStepCount, CommandCount, IncidentCount);
        if (ExecutionStatus == ExecutionStatus.Pending)
        {
            Require(RuntimeSessionId is null && StartedAtUtc is null && CompletedAtUtc is null,
                "Pending Operation Run contains execution timestamps.");
            Require(FailureCode is null && FailureReason is null && ExecutionEvidence is null
                && RecoveryDecisionId is null
                && _outputs.Count == 0 && _fencingTokens.Count == 0,
                "Pending Operation Run contains execution evidence.");
            return;
        }

        if (ExecutionStatus == ExecutionStatus.Canceled
            && RuntimeSessionId is null
            && StartedAtUtc is null)
        {
            Require(CompletedAtUtc is { } completedAtUtc
                    && completedAtUtc != default
                    && completedAtUtc.Offset == TimeSpan.Zero,
                "Operation Run canceled before execution requires a non-default UTC completion timestamp.");
            Require(Judgement == ResultJudgement.Aborted,
                "Operation Run canceled before execution must be judged Aborted.");
            var ordinaryCancellation = string.Equals(
                    FailureCode,
                    PreExecutionCancellationFailureCode,
                    StringComparison.Ordinal)
                && FailureReason is not null;
            var reworkSupersession = string.Equals(
                    FailureCode,
                    ReworkSupersededFailureCode,
                    StringComparison.Ordinal)
                && string.Equals(
                    FailureReason,
                    ReworkSupersededFailureReason,
                    StringComparison.Ordinal);
            Require(ordinaryCancellation || reworkSupersession,
                "Operation Run canceled before execution requires canonical cancellation evidence.");
            Require(CompletedStepCount == 0 && CommandCount == 0 && IncidentCount == 0
                && ExecutionEvidence is null
                && _outputs.Count == 0 && _fencingTokens.Count == 0,
                "Operation Run canceled before execution cannot contain execution evidence.");
            return;
        }

        Require(RuntimeSessionId is not null && StartedAtUtc is not null,
            "Started Operation Run must identify its Runtime Session.");
        Require(_fencingTokens.Count >= Definition.ResourceRequirements.Count
                && _fencingTokens.Values.All(static token => token > 0)
                && Definition.ResourceRequirements.All(_fencingTokens.ContainsKey),
            "Started Operation Run must retain every static and resolved material resource fencing token.");
        if (ExecutionStatus == ExecutionStatus.Running)
        {
            Require(CompletedAtUtc is null && FailureCode is null && FailureReason is null
                    && ExecutionEvidence is null && RecoveryDecisionId is null,
                "Running Operation Run contains terminal state.");
            return;
        }

        Require(CompletedAtUtc is not null, "Terminal Operation Run requires a completion timestamp.");
        Require(
            RecoveryDecisionId is not null
                ? ExecutionEvidence is null
                : ExecutionEvidence is not null,
            "A started terminal Operation Run requires frozen execution evidence or an explicit Recovery Decision.");
        if (ExecutionEvidence is not null)
        {
            Require(
                RuntimeSessionId?.Value == ExecutionEvidence.RuntimeSessionId
                && string.Equals(OperationRunId, ExecutionEvidence.OperationRunId, StringComparison.Ordinal)
                && string.Equals(OperationId, ExecutionEvidence.OperationId, StringComparison.Ordinal)
                && Attempt == ExecutionEvidence.OperationAttempt
                && string.Equals(StationSystemId, ExecutionEvidence.StationSystemId, StringComparison.Ordinal)
                && CompletedAtUtc == ExecutionEvidence.CompletedAtUtc
                && CompletedStepCount == ExecutionEvidence.Steps.Count(step =>
                    string.Equals(step.Status, "Completed", StringComparison.Ordinal))
                && CommandCount == ExecutionEvidence.Commands.Count
                && IncidentCount == ExecutionEvidence.Incidents.Count,
                "Terminal Operation Run execution evidence differs from its frozen result.");
        }
        if (ExecutionStatus == ExecutionStatus.Completed)
        {
            Require(FailureCode is null && FailureReason is null,
                "Completed Operation Run cannot contain a system failure.");
        }
        else
        {
            Require(FailureCode is not null && FailureReason is not null,
                "Unsuccessful Operation Run requires system failure details.");
            Require(Judgement is ResultJudgement.Unknown or ResultJudgement.Aborted,
                "Unsuccessful execution cannot claim a product quality result.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
