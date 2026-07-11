using System.Text.Json;
using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Contracts;
using ProductionExecutionStatus = OpenLineOps.Runtime.Contracts.ExecutionStatus;

namespace OpenLineOps.Agent.Domain.StationJobs;

public sealed class StationJob : AggregateRoot<StationJobId>
{
    private StationJob(StationJobSnapshot snapshot)
        : base(snapshot.JobId)
    {
        IdempotencyKey = Required(snapshot.IdempotencyKey, nameof(snapshot.IdempotencyKey));
        AgentId = Required(snapshot.AgentId, nameof(snapshot.AgentId));
        StationId = Required(snapshot.StationId, nameof(snapshot.StationId));
        StationSystemId = Required(snapshot.StationSystemId, nameof(snapshot.StationSystemId));
        ProductionRunId = NotEmpty(snapshot.ProductionRunId, nameof(snapshot.ProductionRunId));
        ProductionUnitId = NotEmpty(snapshot.ProductionUnitId, nameof(snapshot.ProductionUnitId));
        RuntimeSessionId = NotEmpty(snapshot.RuntimeSessionId, nameof(snapshot.RuntimeSessionId));
        OperationRunId = snapshot.OperationRunId;
        OperationAttempt = snapshot.OperationAttempt > 0
            ? snapshot.OperationAttempt
            : throw new ArgumentOutOfRangeException(
                nameof(snapshot),
                snapshot.OperationAttempt,
                "Operation attempt must be positive.");
        ProductModelId = Required(snapshot.ProductModelId, nameof(snapshot.ProductModelId));
        ProductionUnitIdentityInputKey = Required(
            snapshot.ProductionUnitIdentityInputKey,
            nameof(snapshot.ProductionUnitIdentityInputKey));
        ProductionUnitIdentityValue = Required(
            snapshot.ProductionUnitIdentityValue,
            nameof(snapshot.ProductionUnitIdentityValue));
        LotId = Optional(snapshot.LotId, nameof(snapshot.LotId));
        CarrierId = Optional(snapshot.CarrierId, nameof(snapshot.CarrierId));
        ProjectId = Required(snapshot.ProjectId, nameof(snapshot.ProjectId));
        ApplicationId = Required(snapshot.ApplicationId, nameof(snapshot.ApplicationId));
        ProjectSnapshotId = Required(snapshot.ProjectSnapshotId, nameof(snapshot.ProjectSnapshotId));
        ProductionLineDefinitionId = Required(
            snapshot.ProductionLineDefinitionId,
            nameof(snapshot.ProductionLineDefinitionId));
        TopologyId = Required(snapshot.TopologyId, nameof(snapshot.TopologyId));
        ActorId = Required(snapshot.ActorId, nameof(snapshot.ActorId));
        PackageContentSha256 = Sha256(snapshot.PackageContentSha256, nameof(snapshot.PackageContentSha256));
        OperationId = Required(snapshot.OperationId, nameof(snapshot.OperationId));
        FlowDefinitionId = Required(snapshot.FlowDefinitionId, nameof(snapshot.FlowDefinitionId));
        FlowVersionId = Required(snapshot.FlowVersionId, nameof(snapshot.FlowVersionId));
        ConfigurationSnapshotId = Required(
            snapshot.ConfigurationSnapshotId,
            nameof(snapshot.ConfigurationSnapshotId));
        RecipeSnapshotId = Required(snapshot.RecipeSnapshotId, nameof(snapshot.RecipeSnapshotId));
        ResourceFences = ValidateFences(
            snapshot.ResourceFences,
            StationId,
            StationSystemId,
            snapshot.RequestedAtUtc);
        InputsJson = CanonicalJson(snapshot.InputsJson, nameof(snapshot.InputsJson));
        Status = snapshot.Status;
        ExecutionStatus = snapshot.ExecutionStatus;
        Judgement = snapshot.Judgement;
        ProgressPercent = snapshot.ProgressPercent;
        ProgressPhase = Optional(snapshot.ProgressPhase, nameof(snapshot.ProgressPhase));
        OutputsJson = snapshot.OutputsJson is null
            ? null
            : CanonicalJson(snapshot.OutputsJson, nameof(snapshot.OutputsJson));
        CompletedStepCount = NonNegative(
            snapshot.CompletedStepCount,
            nameof(snapshot.CompletedStepCount));
        CommandCount = NonNegative(snapshot.CommandCount, nameof(snapshot.CommandCount));
        IncidentCount = NonNegative(snapshot.IncidentCount, nameof(snapshot.IncidentCount));
        FailureCode = Optional(snapshot.FailureCode, nameof(snapshot.FailureCode));
        FailureReason = Optional(snapshot.FailureReason, nameof(snapshot.FailureReason));
        RequestedAtUtc = Utc(snapshot.RequestedAtUtc, nameof(snapshot.RequestedAtUtc));
        AcceptedAtUtc = OptionalUtc(snapshot.AcceptedAtUtc, nameof(snapshot.AcceptedAtUtc));
        StartedAtUtc = OptionalUtc(snapshot.StartedAtUtc, nameof(snapshot.StartedAtUtc));
        LastProgressAtUtc = OptionalUtc(snapshot.LastProgressAtUtc, nameof(snapshot.LastProgressAtUtc));
        CompletedAtUtc = OptionalUtc(snapshot.CompletedAtUtc, nameof(snapshot.CompletedAtUtc));
        ValidateState();
    }

    public string IdempotencyKey { get; }
    public string AgentId { get; }
    public string StationId { get; }
    public string StationSystemId { get; }
    public Guid ProductionRunId { get; }
    public Guid ProductionUnitId { get; }
    public Guid RuntimeSessionId { get; }
    public StationOperationRunId OperationRunId { get; }
    public int OperationAttempt { get; }
    public string ProductModelId { get; }
    public string ProductionUnitIdentityInputKey { get; }
    public string ProductionUnitIdentityValue { get; }
    public string? LotId { get; }
    public string? CarrierId { get; }
    public string ProjectId { get; }
    public string ApplicationId { get; }
    public string ProjectSnapshotId { get; }
    public string ProductionLineDefinitionId { get; }
    public string TopologyId { get; }
    public string ActorId { get; }
    public string PackageContentSha256 { get; }
    public string OperationId { get; }
    public string FlowDefinitionId { get; }
    public string FlowVersionId { get; }
    public string ConfigurationSnapshotId { get; }
    public string RecipeSnapshotId { get; }
    public IReadOnlyList<StationResourceFenceEvidence> ResourceFences { get; }
    public string InputsJson { get; }
    public StationJobStatus Status { get; private set; }
    public ExecutionStatus? ExecutionStatus { get; private set; }
    public ResultJudgement? Judgement { get; private set; }
    public int ProgressPercent { get; private set; }
    public string? ProgressPhase { get; private set; }
    public string? OutputsJson { get; private set; }
    public int CompletedStepCount { get; private set; }
    public int CommandCount { get; private set; }
    public int IncidentCount { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; }
    public DateTimeOffset? AcceptedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? LastProgressAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public bool IsTerminal => Status is StationJobStatus.Completed
        or StationJobStatus.Failed
        or StationJobStatus.TimedOut
        or StationJobStatus.Canceled
        or StationJobStatus.Rejected;

    public static StationJob Request(StationJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Restore(new StationJobSnapshot(
            request.JobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.StationSystemId,
            request.ProductionRunId,
            request.ProductionUnitId,
            request.RuntimeSessionId,
            request.OperationRunId,
            request.OperationAttempt,
            request.ProductModelId,
            request.ProductionUnitIdentityInputKey,
            request.ProductionUnitIdentityValue,
            request.LotId,
            request.CarrierId,
            request.ProjectId,
            request.ApplicationId,
            request.ProjectSnapshotId,
            request.ProductionLineDefinitionId,
            request.TopologyId,
            request.ActorId,
            request.PackageContentSha256,
            request.OperationId,
            request.FlowDefinitionId,
            request.FlowVersionId,
            request.ConfigurationSnapshotId,
            request.RecipeSnapshotId,
            request.ResourceFences,
            request.InputsJson,
            StationJobStatus.Requested,
            null,
            null,
            0,
            null,
            null,
            0,
            0,
            0,
            null,
            null,
            request.RequestedAtUtc,
            null,
            null,
            null,
            null));
    }

    public static StationJob Restore(StationJobSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new StationJob(snapshot);
    }

    public void Accept(DateTimeOffset acceptedAtUtc)
    {
        RequireStatus(StationJobStatus.Requested);
        AcceptedAtUtc = Utc(acceptedAtUtc, nameof(acceptedAtUtc));
        EnsureNotBefore(AcceptedAtUtc.Value, RequestedAtUtc, nameof(acceptedAtUtc));
        Status = StationJobStatus.Accepted;
    }

    public void Start(DateTimeOffset startedAtUtc)
    {
        RequireStatus(StationJobStatus.Accepted);
        StartedAtUtc = Utc(startedAtUtc, nameof(startedAtUtc));
        EnsureNotBefore(StartedAtUtc.Value, AcceptedAtUtc!.Value, nameof(startedAtUtc));
        if (ResourceFences.Any(fence => fence.ExpiresAtUtc <= StartedAtUtc.Value))
        {
            throw new InvalidOperationException(
                $"Station job {Id} cannot start with an expired resource fence.");
        }
        Status = StationJobStatus.Running;
        ExecutionStatus = ProductionExecutionStatus.Running;
    }

    public void ReportProgress(int percent, string phase, DateTimeOffset progressedAtUtc)
    {
        RequireStatus(StationJobStatus.Running);
        if (percent is < 0 or > 100 || percent < ProgressPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "Progress must be monotonic from 0 through 100.");
        }

        var atUtc = Utc(progressedAtUtc, nameof(progressedAtUtc));
        EnsureNotBefore(atUtc, LastProgressAtUtc ?? StartedAtUtc!.Value, nameof(progressedAtUtc));
        ProgressPercent = percent;
        ProgressPhase = Required(phase, nameof(phase));
        LastProgressAtUtc = atUtc;
    }

    public void Complete(StationJobCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        RequireStatus(StationJobStatus.Running);
        if (completion.ExecutionStatus is ProductionExecutionStatus.Pending
            or ProductionExecutionStatus.Running)
        {
            throw new ArgumentException("Station job completion requires a terminal execution status.", nameof(completion));
        }

        var completedAtUtc = Utc(completion.CompletedAtUtc, nameof(completion.CompletedAtUtc));
        EnsureNotBefore(completedAtUtc, LastProgressAtUtc ?? StartedAtUtc!.Value, nameof(completion.CompletedAtUtc));
        ExecutionStatus = completion.ExecutionStatus;
        Judgement = completion.Judgement;
        OutputsJson = CanonicalJson(completion.OutputsJson, nameof(completion.OutputsJson));
        CompletedStepCount = NonNegative(
            completion.CompletedStepCount,
            nameof(completion.CompletedStepCount));
        CommandCount = NonNegative(completion.CommandCount, nameof(completion.CommandCount));
        IncidentCount = NonNegative(completion.IncidentCount, nameof(completion.IncidentCount));
        FailureCode = Optional(completion.FailureCode, nameof(completion.FailureCode));
        FailureReason = Optional(completion.FailureReason, nameof(completion.FailureReason));
        CompletedAtUtc = completedAtUtc;
        ProgressPercent = completion.ExecutionStatus == ProductionExecutionStatus.Completed
            ? 100
            : ProgressPercent;
        Status = completion.ExecutionStatus switch
        {
            ProductionExecutionStatus.Completed => StationJobStatus.Completed,
            ProductionExecutionStatus.Failed => StationJobStatus.Failed,
            ProductionExecutionStatus.TimedOut => StationJobStatus.TimedOut,
            ProductionExecutionStatus.Canceled => StationJobStatus.Canceled,
            ProductionExecutionStatus.Rejected => StationJobStatus.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };
        ValidateTerminalResult();
    }

    public void RejectBeforeStart(string code, string reason, DateTimeOffset rejectedAtUtc)
    {
        RequireStatus(StationJobStatus.Accepted);
        var atUtc = Utc(rejectedAtUtc, nameof(rejectedAtUtc));
        EnsureNotBefore(atUtc, AcceptedAtUtc!.Value, nameof(rejectedAtUtc));
        ExecutionStatus = ProductionExecutionStatus.Rejected;
        Judgement = ResultJudgement.Unknown;
        OutputsJson = "{}";
        FailureCode = Required(code, nameof(code));
        FailureReason = Required(reason, nameof(reason));
        CompletedAtUtc = atUtc;
        Status = StationJobStatus.Rejected;
        ValidateTerminalResult();
    }

    public void Cancel(string reason, DateTimeOffset canceledAtUtc)
    {
        if (Status is not (StationJobStatus.Accepted or StationJobStatus.Running))
        {
            throw new InvalidOperationException(
                $"Station job {Id} cannot be canceled from {Status}.");
        }

        var atUtc = Utc(canceledAtUtc, nameof(canceledAtUtc));
        EnsureNotBefore(
            atUtc,
            LastProgressAtUtc ?? StartedAtUtc ?? AcceptedAtUtc!.Value,
            nameof(canceledAtUtc));
        ExecutionStatus = ProductionExecutionStatus.Canceled;
        Judgement = ResultJudgement.Aborted;
        OutputsJson = "{}";
        FailureCode = "Agent.ExecutionCanceled";
        FailureReason = Required(reason, nameof(reason));
        CompletedAtUtc = atUtc;
        Status = StationJobStatus.Canceled;
        ValidateTerminalResult();
    }

    public void RequireRecovery(string reason)
    {
        if (Status is not (StationJobStatus.Accepted or StationJobStatus.Running))
        {
            throw new InvalidOperationException($"Station job {Id} cannot require recovery from {Status}.");
        }

        Status = StationJobStatus.RecoveryRequired;
        ExecutionStatus = ProductionExecutionStatus.Failed;
        Judgement = ResultJudgement.Unknown;
        FailureCode = "Agent.RecoveryRequired";
        FailureReason = Required(reason, nameof(reason));
    }

    public StationJobSnapshot ToSnapshot() => new(
        Id,
        IdempotencyKey,
        AgentId,
        StationId,
        StationSystemId,
        ProductionRunId,
        ProductionUnitId,
        RuntimeSessionId,
        OperationRunId,
        OperationAttempt,
        ProductModelId,
        ProductionUnitIdentityInputKey,
        ProductionUnitIdentityValue,
        LotId,
        CarrierId,
        ProjectId,
        ApplicationId,
        ProjectSnapshotId,
        ProductionLineDefinitionId,
        TopologyId,
        ActorId,
        PackageContentSha256,
        OperationId,
        FlowDefinitionId,
        FlowVersionId,
        ConfigurationSnapshotId,
        RecipeSnapshotId,
        ResourceFences,
        InputsJson,
        Status,
        ExecutionStatus,
        Judgement,
        ProgressPercent,
        ProgressPhase,
        OutputsJson,
        CompletedStepCount,
        CommandCount,
        IncidentCount,
        FailureCode,
        FailureReason,
        RequestedAtUtc,
        AcceptedAtUtc,
        StartedAtUtc,
        LastProgressAtUtc,
        CompletedAtUtc);

    private void ValidateState()
    {
        if (ProgressPercent is < 0 or > 100)
        {
            throw new InvalidDataException("Station job progress is outside 0 through 100.");
        }

        if (IsTerminal)
        {
            ValidateTerminalResult();
        }
        else if (CompletedAtUtc is not null
                 || OutputsJson is not null
                 || CompletedStepCount != 0
                 || CommandCount != 0
                 || IncidentCount != 0)
        {
            throw new InvalidDataException("Non-terminal station job contains terminal output.");
        }
    }

    private void ValidateTerminalResult()
    {
        if (ExecutionStatus is null || Judgement is null || CompletedAtUtc is null)
        {
            throw new InvalidDataException("Terminal station job requires execution status, judgement and completion time.");
        }

        var hasFailure = FailureCode is not null && FailureReason is not null;
        if (ExecutionStatus == ProductionExecutionStatus.Completed
            && (hasFailure || Judgement == ResultJudgement.Unknown))
        {
            throw new InvalidDataException(
                "Completed station execution requires a product judgement and cannot contain a system failure.");
        }

        if (ExecutionStatus == ProductionExecutionStatus.Canceled
            && (!hasFailure || Judgement != ResultJudgement.Aborted))
        {
            throw new InvalidDataException(
                "Canceled station execution requires Aborted judgement and failure evidence.");
        }

        if (ExecutionStatus is ProductionExecutionStatus.Failed
                or ProductionExecutionStatus.TimedOut
                or ProductionExecutionStatus.Rejected
            && (!hasFailure || Judgement != ResultJudgement.Unknown))
        {
            throw new InvalidDataException(
                "Failed station execution requires Unknown judgement and failure evidence.");
        }
    }

    private void RequireStatus(StationJobStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Station job {Id} must be {expected}, not {Status}.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException($"{parameterName} must be canonical non-empty text.", parameterName)
            : value;

    private static string? Optional(string? value, string parameterName) =>
        value is null ? null : Required(value, parameterName);

    private static Guid NotEmpty(Guid value, string parameterName) =>
        value == Guid.Empty ? throw new ArgumentException($"{parameterName} cannot be empty.", parameterName) : value;

    private static int NonNegative(int value, string parameterName) =>
        value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, "Execution metric cannot be negative.");

    private static string Sha256(string value, string parameterName) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new ArgumentException($"{parameterName} must be a lowercase SHA-256.", parameterName);

    private static string CanonicalJson(string value, string parameterName)
    {
        Required(value, parameterName);
        using var document = JsonDocument.Parse(value);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static DateTimeOffset Utc(DateTimeOffset value, string parameterName) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new ArgumentException($"{parameterName} must use UTC offset zero.", parameterName);

    private static DateTimeOffset? OptionalUtc(DateTimeOffset? value, string parameterName) =>
        value is null ? null : Utc(value.Value, parameterName);

    private static void EnsureNotBefore(DateTimeOffset value, DateTimeOffset minimum, string parameterName)
    {
        if (value < minimum)
        {
            throw new ArgumentException($"{parameterName} cannot precede the prior station job transition.", parameterName);
        }
    }

    private static StationResourceFenceEvidence[] ValidateFences(
        IReadOnlyList<StationResourceFenceEvidence> fences,
        string stationId,
        string stationSystemId,
        DateTimeOffset requestedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(fences);
        var result = fences
            .Select(fence => new StationResourceFenceEvidence(
                Required(fence.ResourceKind, nameof(fence.ResourceKind)),
                Required(fence.ResourceId, nameof(fence.ResourceId)),
                fence.FencingToken > 0
                    ? fence.FencingToken
                    : throw new ArgumentOutOfRangeException(
                        nameof(fences),
                        fence.FencingToken,
                        "Fencing token must be positive."),
                Utc(fence.ExpiresAtUtc, nameof(fence.ExpiresAtUtc))))
            .ToArray();
        if (result.Select(fence => (fence.ResourceKind, fence.ResourceId))
            .Distinct().Count() != result.Length)
        {
            throw new ArgumentException("Station job resource fences must be unique.", nameof(fences));
        }

        if (!result.Any(fence => string.Equals(fence.ResourceKind, "Station", StringComparison.Ordinal)
            && (string.Equals(fence.ResourceId, stationId, StringComparison.Ordinal)
                || string.Equals(fence.ResourceId, stationSystemId, StringComparison.Ordinal))))
        {
            throw new ArgumentException(
                "Station job resource fences must include the target Station.",
                nameof(fences));
        }

        if (result.Any(fence => fence.ExpiresAtUtc <= requestedAtUtc))
        {
            throw new ArgumentException(
                "Station job resource fences must expire after the request time.",
                nameof(fences));
        }

        return result;
    }
}

public sealed record StationJobRequest(
    StationJobId JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    Guid ProductionUnitId,
    Guid RuntimeSessionId,
    StationOperationRunId OperationRunId,
    int OperationAttempt,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string ProductionLineDefinitionId,
    string TopologyId,
    string ActorId,
    string PackageContentSha256,
    string OperationId,
    string FlowDefinitionId,
    string FlowVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    IReadOnlyList<StationResourceFenceEvidence> ResourceFences,
    string InputsJson,
    DateTimeOffset RequestedAtUtc);

public sealed record StationJobCompletion(
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    string OutputsJson,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset CompletedAtUtc);

public sealed record StationResourceFenceEvidence(
    string ResourceKind,
    string ResourceId,
    long FencingToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record StationJobSnapshot(
    StationJobId JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    Guid ProductionUnitId,
    Guid RuntimeSessionId,
    StationOperationRunId OperationRunId,
    int OperationAttempt,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string ProductionLineDefinitionId,
    string TopologyId,
    string ActorId,
    string PackageContentSha256,
    string OperationId,
    string FlowDefinitionId,
    string FlowVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    IReadOnlyList<StationResourceFenceEvidence> ResourceFences,
    string InputsJson,
    StationJobStatus Status,
    ExecutionStatus? ExecutionStatus,
    ResultJudgement? Judgement,
    int ProgressPercent,
    string? ProgressPhase,
    string? OutputsJson,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? LastProgressAtUtc,
    DateTimeOffset? CompletedAtUtc);
