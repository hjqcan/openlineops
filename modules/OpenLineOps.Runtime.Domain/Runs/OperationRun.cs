using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed class OperationRun
{
    private readonly Dictionary<string, ProductionContextValue> _outputs;
    private readonly Dictionary<ResourceRequirement, long> _fencingTokens;

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
        IReadOnlyDictionary<string, ProductionContextValue>? outputs,
        IReadOnlyDictionary<ResourceRequirement, long>? fencingTokens)
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
        _outputs = outputs?.ToDictionary() ?? [];
        _fencingTokens = fencingTokens?.ToDictionary() ?? [];
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

    public IReadOnlyDictionary<string, ProductionContextValue> Outputs => _outputs;

    public IReadOnlyDictionary<ResourceRequirement, long> FencingTokens => _fencingTokens;

    public bool IsTerminal => ExecutionStatus is ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;

    private OperationRunDefinition Definition { get; }

    internal static OperationRun Create(OperationRunDefinition definition, int attempt)
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
            null);
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
            snapshot.Outputs,
            snapshot.FencingTokens);
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

        SetMetrics(completedStepCount, commandCount, incidentCount);
        _outputs.Clear();
        foreach (var output in outputs ?? new Dictionary<string, ProductionContextValue>())
        {
            var key = ProductionRunText.Required(output.Key, "output key");
            _outputs.Add(key, output.Value ?? throw new ArgumentException("Operation output cannot be null."));
        }

        ExecutionStatus = ExecutionStatus.Completed;
        Judgement = judgement;
        CompletedAtUtc = completedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    internal RuntimeOperationResult Fail(
        ExecutionStatus terminalStatus,
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

        SetMetrics(completedStepCount, commandCount, incidentCount);
        ExecutionStatus = terminalStatus;
        Judgement = ResultJudgement.Unknown;
        CompletedAtUtc = completedAtUtc;
        FailureCode = ProductionRunText.Required(code, nameof(code));
        FailureReason = ProductionRunText.Required(reason, nameof(reason));
        return RuntimeOperationResult.Accepted();
    }

    internal void Cancel(string reason, DateTimeOffset canceledAtUtc)
    {
        if (IsTerminal)
        {
            return;
        }

        ExecutionStatus = ExecutionStatus.Canceled;
        Judgement = ResultJudgement.Aborted;
        CompletedAtUtc = canceledAtUtc;
        FailureCode = "Runtime.ProductionRunStopped";
        FailureReason = ProductionRunText.Required(reason, nameof(reason));
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
        new Dictionary<string, ProductionContextValue>(_outputs, StringComparer.Ordinal),
        new Dictionary<ResourceRequirement, long>(_fencingTokens));

    private static string CreateOperationRunId(string operationId, int attempt) =>
        $"{operationId}@{attempt:D4}";

    private void SetMetrics(int completedStepCount, int commandCount, int incidentCount)
    {
        if (completedStepCount < 0 || commandCount < 0 || incidentCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedStepCount),
                "Operation execution metrics cannot be negative.");
        }

        CompletedStepCount = completedStepCount;
        CommandCount = commandCount;
        IncidentCount = incidentCount;
    }

    private void ValidateState()
    {
        SetMetrics(CompletedStepCount, CommandCount, IncidentCount);
        if (ExecutionStatus == ExecutionStatus.Pending)
        {
            Require(RuntimeSessionId is null && StartedAtUtc is null && CompletedAtUtc is null,
                "Pending Operation Run contains execution timestamps.");
            Require(FailureCode is null && FailureReason is null && _outputs.Count == 0
                && _fencingTokens.Count == 0,
                "Pending Operation Run contains execution evidence.");
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
            Require(CompletedAtUtc is null && FailureCode is null && FailureReason is null,
                "Running Operation Run contains terminal state.");
            return;
        }

        Require(CompletedAtUtc is not null, "Terminal Operation Run requires a completion timestamp.");
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
