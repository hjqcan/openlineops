using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed class TraceRecord : AggregateRoot<TraceRecordId>
{
    private readonly TraceStageExecution[] _stages;
    private readonly AuditEntry[] _auditEntries;

    private TraceRecord(
        TraceRecordId id,
        ProductionRunId productionRunId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        string dutModelId,
        string dutIdentityInputKey,
        string dutIdentityValue,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        ActorId actorId,
        TraceProductionRunStatus runStatus,
        ResultJudgement judgement,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? failureCode,
        string? failureReason,
        IEnumerable<TraceStageExecution> stages,
        IEnumerable<AuditEntry> auditEntries)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(auditEntries);

        if (id.Value != productionRunId.Value)
        {
            throw new ArgumentException(
                "TraceRecordId must equal ProductionRunId for deterministic run trace identity.",
                nameof(id));
        }

        if (createdAtUtc == default || completedAtUtc == default)
        {
            throw new ArgumentException("Run trace timestamps are required.", nameof(completedAtUtc));
        }

        if (startedAtUtc < createdAtUtc || completedAtUtc < (startedAtUtc ?? createdAtUtc))
        {
            throw new ArgumentException("Run trace timestamps must be chronological.", nameof(completedAtUtc));
        }

        ProductionRunId = productionRunId;
        ProjectId = TraceabilityIdGuard.NotBlank(projectId, nameof(projectId));
        ApplicationId = TraceabilityIdGuard.NotBlank(applicationId, nameof(applicationId));
        ProjectSnapshotId = TraceabilityIdGuard.NotBlank(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = TraceabilityIdGuard.NotBlank(topologyId, nameof(topologyId));
        ProductionLineDefinitionId = TraceabilityIdGuard.NotBlank(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        DutModelId = TraceabilityIdGuard.NotBlank(dutModelId, nameof(dutModelId));
        DutIdentityInputKey = TraceabilityIdGuard.NotBlank(
            dutIdentityInputKey,
            nameof(dutIdentityInputKey));
        DutIdentityValue = TraceabilityIdGuard.NotBlank(dutIdentityValue, nameof(dutIdentityValue));
        BatchId = TraceabilityIdGuard.OptionalText(batchId);
        FixtureId = TraceabilityIdGuard.OptionalText(fixtureId);
        DeviceId = TraceabilityIdGuard.OptionalText(deviceId);
        ActorId = actorId;
        RunStatus = runStatus;
        Judgement = judgement;
        CreatedAtUtc = createdAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        FailureCode = TraceabilityIdGuard.OptionalText(failureCode);
        FailureReason = TraceabilityIdGuard.OptionalText(failureReason);
        _stages = stages.OrderBy(stage => stage.Sequence).ToArray();
        _auditEntries = auditEntries.ToArray();

        ValidateStages(_stages);
        ValidateTerminalState();

        if (_auditEntries.Select(entry => entry.Id).Distinct().Count() != _auditEntries.Length)
        {
            throw new ArgumentException("Audit entry ids must be unique.", nameof(auditEntries));
        }
    }

    public ProductionRunId ProductionRunId { get; }
    public string ProjectId { get; }
    public string ApplicationId { get; }
    public string ProjectSnapshotId { get; }
    public string TopologyId { get; }
    public string ProductionLineDefinitionId { get; }
    public string DutModelId { get; }
    public string DutIdentityInputKey { get; }
    public string DutIdentityValue { get; }
    public string? BatchId { get; }
    public string? FixtureId { get; }
    public string? DeviceId { get; }
    public ActorId ActorId { get; }
    public TraceProductionRunStatus RunStatus { get; }
    public ResultJudgement Judgement { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public string? FailureCode { get; }
    public string? FailureReason { get; }
    public IReadOnlyList<TraceStageExecution> Stages => _stages;
    public IReadOnlyList<AuditEntry> AuditEntries => _auditEntries;

    public static TraceRecord Create(
        TraceRecordId id,
        ProductionRunId productionRunId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        string dutModelId,
        string dutIdentityInputKey,
        string dutIdentityValue,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        ActorId actorId,
        TraceProductionRunStatus runStatus,
        ResultJudgement judgement,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? failureCode,
        string? failureReason,
        IEnumerable<TraceStageExecution> stages,
        IEnumerable<AuditEntry> auditEntries)
    {
        return new TraceRecord(
            id,
            productionRunId,
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId,
            productionLineDefinitionId,
            dutModelId,
            dutIdentityInputKey,
            dutIdentityValue,
            batchId,
            fixtureId,
            deviceId,
            actorId,
            runStatus,
            judgement,
            createdAtUtc,
            startedAtUtc,
            completedAtUtc,
            failureCode,
            failureReason,
            stages,
            auditEntries);
    }

    public static TraceRecord Restore(
        TraceRecordId id,
        ProductionRunId productionRunId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        string dutModelId,
        string dutIdentityInputKey,
        string dutIdentityValue,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        ActorId actorId,
        TraceProductionRunStatus runStatus,
        ResultJudgement judgement,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? failureCode,
        string? failureReason,
        IEnumerable<TraceStageExecution> stages,
        IEnumerable<AuditEntry> auditEntries)
    {
        return Create(
            id,
            productionRunId,
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId,
            productionLineDefinitionId,
            dutModelId,
            dutIdentityInputKey,
            dutIdentityValue,
            batchId,
            fixtureId,
            deviceId,
            actorId,
            runStatus,
            judgement,
            createdAtUtc,
            startedAtUtc,
            completedAtUtc,
            failureCode,
            failureReason,
            stages,
            auditEntries);
    }

    private static void ValidateStages(TraceStageExecution[] stages)
    {
        if (stages.Length == 0)
        {
            throw new ArgumentException("A production run trace must contain at least one stage.", nameof(stages));
        }

        if (stages.Select(stage => stage.StageId).Distinct(StringComparer.Ordinal).Count() != stages.Length)
        {
            throw new ArgumentException("Production stage ids must be unique.", nameof(stages));
        }

        for (var index = 0; index < stages.Length; index++)
        {
            if (stages[index].Sequence != index + 1)
            {
                throw new ArgumentException(
                    "Production stage sequence must be contiguous from one.",
                    nameof(stages));
            }
        }
    }

    private void ValidateTerminalState()
    {
        switch (RunStatus)
        {
            case TraceProductionRunStatus.Completed:
                if (FailureCode is not null
                    || FailureReason is not null
                    || Stages.Any(stage => stage.Status != TraceStageStatus.Completed))
                {
                    throw new ArgumentException("Completed run trace contains failure or incomplete stage state.");
                }

                break;
            case TraceProductionRunStatus.Failed:
                if (FailureCode is null
                    || FailureReason is null
                    || Stages.All(stage => stage.Status != TraceStageStatus.Failed))
                {
                    throw new ArgumentException("Failed run trace must freeze its failure and failed stage.");
                }

                break;
            case TraceProductionRunStatus.Canceled:
                if (FailureCode is null
                    || FailureReason is null
                    || Stages.Any(stage => stage.Status is TraceStageStatus.Failed))
                {
                    throw new ArgumentException("Canceled run trace contains invalid terminal state.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(RunStatus), RunStatus, "Run trace status must be terminal.");
        }
    }
}
