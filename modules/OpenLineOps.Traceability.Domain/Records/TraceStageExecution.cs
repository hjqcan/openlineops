using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed record TraceStageExecution
{
    public TraceStageExecution(
        string stageId,
        int sequence,
        string workstationId,
        StationId stationId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        RuntimeSessionId? runtimeSessionId,
        TraceRuntimeSessionStatus? runtimeSessionStatus,
        TraceStageStatus status,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? failureCode,
        string? failureReason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        IEnumerable<TraceCommandRecord> commands,
        IEnumerable<MeasurementRecord> measurements,
        IEnumerable<ArtifactRecord> artifacts,
        IEnumerable<TraceIncidentRecord> incidents)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(incidents);

        StageId = TraceabilityIdGuard.NotBlank(stageId, nameof(stageId));
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Stage sequence must be positive.");
        }

        Sequence = sequence;
        WorkstationId = TraceabilityIdGuard.NotBlank(workstationId, nameof(workstationId));
        StationId = stationId;
        ProcessDefinitionId = processDefinitionId;
        ProcessVersionId = processVersionId;
        ConfigurationSnapshotId = configurationSnapshotId;
        RecipeSnapshotId = recipeSnapshotId;
        RuntimeSessionId = runtimeSessionId;
        RuntimeSessionStatus = runtimeSessionStatus;
        Status = status;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc == default
            ? throw new ArgumentException("Stage completion timestamp is required.", nameof(completedAtUtc))
            : completedAtUtc;
        FailureCode = TraceabilityIdGuard.OptionalText(failureCode);
        FailureReason = TraceabilityIdGuard.OptionalText(failureReason);
        CompletedStepCount = NonNegative(completedStepCount, nameof(completedStepCount));
        CommandCount = NonNegative(commandCount, nameof(commandCount));
        IncidentCount = NonNegative(incidentCount, nameof(incidentCount));
        Commands = commands.ToArray();
        Measurements = measurements.ToArray();
        Artifacts = artifacts.ToArray();
        Incidents = incidents.ToArray();

        if (startedAtUtc > CompletedAtUtc)
        {
            throw new ArgumentException("Stage completion time cannot precede start time.", nameof(completedAtUtc));
        }

        if (runtimeSessionId is null && RuntimeSessionStatus is not null)
        {
            throw new ArgumentException("A runtime session status requires a runtime session id.", nameof(runtimeSessionStatus));
        }

        if (Commands.Select(command => command.RuntimeCommandId).Distinct().Count() != Commands.Count)
        {
            throw new ArgumentException("Stage command ids must be unique.", nameof(commands));
        }

        if (Measurements.Select(measurement => measurement.Id).Distinct().Count() != Measurements.Count)
        {
            throw new ArgumentException("Stage measurement ids must be unique.", nameof(measurements));
        }

        if (Artifacts.Select(artifact => artifact.Id).Distinct().Count() != Artifacts.Count)
        {
            throw new ArgumentException("Stage artifact ids must be unique.", nameof(artifacts));
        }

        if (Incidents.Select(incident => incident.RuntimeIncidentId).Distinct().Count() != Incidents.Count)
        {
            throw new ArgumentException("Stage incident ids must be unique.", nameof(incidents));
        }

        if (CommandCount != Commands.Count || IncidentCount != Incidents.Count)
        {
            throw new ArgumentException("Stage evidence counts must equal their frozen evidence collections.");
        }

        ValidateTerminalState();
    }

    public string StageId { get; }
    public int Sequence { get; }
    public string WorkstationId { get; }
    public StationId StationId { get; }
    public ProcessDefinitionId ProcessDefinitionId { get; }
    public ProcessVersionId ProcessVersionId { get; }
    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }
    public RecipeSnapshotId RecipeSnapshotId { get; }
    public RuntimeSessionId? RuntimeSessionId { get; }
    public TraceRuntimeSessionStatus? RuntimeSessionStatus { get; }
    public TraceStageStatus Status { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public string? FailureCode { get; }
    public string? FailureReason { get; }
    public int CompletedStepCount { get; }
    public int CommandCount { get; }
    public int IncidentCount { get; }
    public IReadOnlyCollection<TraceCommandRecord> Commands { get; }
    public IReadOnlyCollection<MeasurementRecord> Measurements { get; }
    public IReadOnlyCollection<ArtifactRecord> Artifacts { get; }
    public IReadOnlyCollection<TraceIncidentRecord> Incidents { get; }

    private static int NonNegative(int value, string parameterName)
    {
        return value < 0
            ? throw new ArgumentOutOfRangeException(parameterName, "Evidence counts cannot be negative.")
            : value;
    }

    private void ValidateTerminalState()
    {
        switch (Status)
        {
            case TraceStageStatus.Completed:
                if (RuntimeSessionId is null
                    || StartedAtUtc is null
                    || RuntimeSessionStatus != TraceRuntimeSessionStatus.Completed
                    || FailureCode is not null
                    || FailureReason is not null)
                {
                    throw new ArgumentException("Completed Stage trace contains invalid lifecycle evidence.");
                }

                break;
            case TraceStageStatus.Failed:
                if (FailureCode is null || FailureReason is null)
                {
                    throw new ArgumentException("Failed Stage trace must freeze its failure.");
                }

                break;
            case TraceStageStatus.Canceled:
                if (RuntimeSessionId is null
                    || StartedAtUtc is null
                    || FailureCode is null
                    || FailureReason is null)
                {
                    throw new ArgumentException("Canceled Stage trace contains invalid lifecycle evidence.");
                }

                break;
            case TraceStageStatus.Skipped:
                if (RuntimeSessionId is not null
                    || RuntimeSessionStatus is not null
                    || StartedAtUtc is not null
                    || FailureCode is not null
                    || FailureReason is null
                    || CompletedStepCount != 0
                    || CommandCount != 0
                    || IncidentCount != 0
                    || Commands.Count != 0
                    || Measurements.Count != 0
                    || Artifacts.Count != 0
                    || Incidents.Count != 0)
                {
                    throw new ArgumentException("Skipped Stage trace contains execution evidence.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Status), Status, "Stage trace status must be terminal.");
        }
    }
}
