using System.Globalization;
using System.Text.Json;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed record TraceOperationExecution
{
    public TraceOperationExecution(
        string operationRunId,
        string operationId,
        int attempt,
        string stationSystemId,
        StationId stationId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId,
        RuntimeSessionId? runtimeSessionId,
        TraceRuntimeSessionStatus? runtimeSessionStatus,
        ExecutionStatus executionStatus,
        ResultJudgement judgement,
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
        IEnumerable<TraceIncidentRecord> incidents,
        IEnumerable<TraceOperationOutput> outputs,
        IEnumerable<TraceResourceFencingToken> fencingTokens)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(incidents);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(fencingTokens);

        OperationRunId = TraceabilityIdGuard.NotBlank(operationRunId, nameof(operationRunId));
        OperationId = TraceabilityIdGuard.NotBlank(operationId, nameof(operationId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        Attempt = attempt;
        StationSystemId = TraceabilityIdGuard.NotBlank(stationSystemId, nameof(stationSystemId));
        StationId = stationId;
        ProcessDefinitionId = processDefinitionId;
        ProcessVersionId = processVersionId;
        ConfigurationSnapshotId = configurationSnapshotId;
        RecipeSnapshotId = recipeSnapshotId;
        RuntimeSessionId = runtimeSessionId;
        RuntimeSessionStatus = runtimeSessionStatus;
        ExecutionStatus = Enum.IsDefined(executionStatus)
            ? executionStatus
            : throw new ArgumentOutOfRangeException(nameof(executionStatus));
        Judgement = Enum.IsDefined(judgement)
            ? judgement
            : throw new ArgumentOutOfRangeException(nameof(judgement));
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc == default
            ? throw new ArgumentException("Operation completion timestamp is required.", nameof(completedAtUtc))
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
        Outputs = outputs.ToArray();
        FencingTokens = fencingTokens.ToArray();

        if (startedAtUtc > CompletedAtUtc)
        {
            throw new ArgumentException("Operation completion time cannot precede start time.", nameof(completedAtUtc));
        }

        EnsureUnique(Commands.Select(command => command.RuntimeCommandId), nameof(commands));
        EnsureUnique(Measurements.Select(measurement => measurement.Id), nameof(measurements));
        EnsureUnique(Artifacts.Select(artifact => artifact.Id), nameof(artifacts));
        EnsureUnique(Incidents.Select(incident => incident.RuntimeIncidentId), nameof(incidents));
        EnsureUnique(Outputs.Select(output => output.Key), nameof(outputs), StringComparer.Ordinal);
        EnsureUnique(
            FencingTokens.Select(token => $"{token.ResourceKind}/{token.ResourceId}"),
            nameof(fencingTokens),
            StringComparer.Ordinal);
        if (CommandCount != Commands.Count || IncidentCount != Incidents.Count)
        {
            throw new ArgumentException("Operation evidence counts must equal their frozen evidence collections.");
        }

        ValidateTerminalState();
    }

    public string OperationRunId { get; }
    public string OperationId { get; }
    public int Attempt { get; }
    public string StationSystemId { get; }
    public StationId StationId { get; }
    public ProcessDefinitionId ProcessDefinitionId { get; }
    public ProcessVersionId ProcessVersionId { get; }
    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }
    public RecipeSnapshotId RecipeSnapshotId { get; }
    public RuntimeSessionId? RuntimeSessionId { get; }
    public TraceRuntimeSessionStatus? RuntimeSessionStatus { get; }
    public ExecutionStatus ExecutionStatus { get; }
    public ResultJudgement Judgement { get; }
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
    public IReadOnlyCollection<TraceOperationOutput> Outputs { get; }
    public IReadOnlyCollection<TraceResourceFencingToken> FencingTokens { get; }

    private void ValidateTerminalState()
    {
        if (ExecutionStatus is not (ExecutionStatus.Completed
            or ExecutionStatus.Failed
            or ExecutionStatus.TimedOut
            or ExecutionStatus.Canceled
            or ExecutionStatus.Rejected))
        {
            throw new ArgumentException("Trace operations must contain terminal execution state.");
        }

        var hasRuntimeSessionEvidence = RuntimeSessionId is not null
            || RuntimeSessionStatus is not null
            || StartedAtUtc is not null;
        if (!hasRuntimeSessionEvidence)
        {
            if (ExecutionStatus != ExecutionStatus.Canceled
                || Judgement != ResultJudgement.Aborted
                || FailureCode is null
                || FailureReason is null
                || CompletedStepCount != 0
                || CommandCount != 0
                || IncidentCount != 0
                || Commands.Count != 0
                || Measurements.Count != 0
                || Artifacts.Count != 0
                || Incidents.Count != 0
                || Outputs.Count != 0)
            {
                throw new ArgumentException(
                    "Only an Operation canceled before dispatch may omit Runtime Session evidence.");
            }

            return;
        }

        if (RuntimeSessionId is null
            || RuntimeSessionStatus is null
            || StartedAtUtc is null)
        {
            throw new ArgumentException("Executed Trace operations require complete Runtime Session evidence.");
        }

        if (ExecutionStatus == ExecutionStatus.Completed)
        {
            if (RuntimeSessionStatus is not (TraceRuntimeSessionStatus.Completed
                    or TraceRuntimeSessionStatus.Reconciled)
                || FailureCode is not null
                || FailureReason is not null
                || Judgement == ResultJudgement.Unknown)
            {
                throw new ArgumentException("Completed Trace operation contains invalid execution or judgement evidence.");
            }

            return;
        }

        if (FailureCode is null || FailureReason is null)
        {
            throw new ArgumentException("Unsuccessful Trace operation must freeze its failure.");
        }

        var expectedJudgement = ExecutionStatus == ExecutionStatus.Canceled
            ? ResultJudgement.Aborted
            : ResultJudgement.Unknown;
        if (Judgement != expectedJudgement)
        {
            throw new ArgumentException("Unsuccessful execution cannot claim a product quality result.");
        }

        var runtimeStatusMatches = ExecutionStatus == ExecutionStatus.Canceled
            ? RuntimeSessionStatus is TraceRuntimeSessionStatus.Canceled or TraceRuntimeSessionStatus.Stopped
            : RuntimeSessionStatus == TraceRuntimeSessionStatus.Failed;
        if (!runtimeStatusMatches)
        {
            throw new ArgumentException(
                "Unsuccessful Trace operation Runtime Session status differs from its execution status.");
        }
    }

    private static int NonNegative(int value, string parameterName) => value < 0
        ? throw new ArgumentOutOfRangeException(parameterName, "Evidence counts cannot be negative.")
        : value;

    private static void EnsureUnique<T>(
        IEnumerable<T> values,
        string parameterName,
        IEqualityComparer<T>? comparer = null)
    {
        var materialized = values.ToArray();
        if (materialized.Distinct(comparer).Count() != materialized.Length)
        {
            throw new ArgumentException("Trace operation evidence identities must be unique.", parameterName);
        }
    }
}

public sealed record TraceOperationOutput
{
    public TraceOperationOutput(string key, string valueKind, string canonicalJson)
    {
        Key = TraceabilityIdGuard.NotBlank(key, nameof(key));
        ValueKind = TraceabilityIdGuard.NotBlank(valueKind, nameof(valueKind));
        CanonicalJson = TraceabilityIdGuard.NotBlank(canonicalJson, nameof(canonicalJson));
        ValidateTypedValue();
    }

    public string Key { get; }
    public string ValueKind { get; }
    public string CanonicalJson { get; }

    private void ValidateTypedValue()
    {
        if (!Enum.TryParse<ProductionContextValueKind>(ValueKind, ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind)
            || !string.Equals(kind.ToString(), ValueKind, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Operation output value kind '{ValueKind}' is not an exact supported token.",
                nameof(ValueKind));
        }

        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(CanonicalJson);
            value = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Operation output must contain valid canonical JSON.",
                nameof(CanonicalJson),
                exception);
        }

        if (!string.Equals(value.GetRawText(), CanonicalJson, StringComparison.Ordinal)
            || !MatchesKind(kind, value))
        {
            throw new ArgumentException(
                $"Operation output JSON is not canonical for {kind}.",
                nameof(CanonicalJson));
        }
    }

    private static bool MatchesKind(ProductionContextValueKind kind, JsonElement value)
    {
        return kind switch
        {
            ProductionContextValueKind.Text => value.ValueKind == JsonValueKind.String,
            ProductionContextValueKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            ProductionContextValueKind.WholeNumber => value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var integer)
                && string.Equals(
                    integer.ToString(CultureInfo.InvariantCulture),
                    value.GetRawText(),
                    StringComparison.Ordinal),
            ProductionContextValueKind.FixedPoint => value.ValueKind == JsonValueKind.Number
                && decimal.TryParse(
                    value.GetRawText(),
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out _),
            ProductionContextValueKind.DateTimeUtc => value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParseExact(
                    value.GetString(),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp)
                && timestamp.Offset == TimeSpan.Zero,
            _ => false
        };
    }
}

public sealed record TraceResourceFencingToken
{
    public TraceResourceFencingToken(string resourceKind, string resourceId, long fencingToken)
    {
        ResourceKind = TraceabilityIdGuard.NotBlank(resourceKind, nameof(resourceKind));
        ResourceId = TraceabilityIdGuard.NotBlank(resourceId, nameof(resourceId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
        FencingToken = fencingToken;
    }

    public string ResourceKind { get; }
    public string ResourceId { get; }
    public long FencingToken { get; }
}

public sealed record TraceRouteDecision
{
    public TraceRouteDecision(
        string sourceOperationRunId,
        string transitionId,
        string targetOperationId,
        ResultJudgement sourceJudgement,
        int traversal,
        DateTimeOffset decidedAtUtc)
    {
        SourceOperationRunId = TraceabilityIdGuard.NotBlank(sourceOperationRunId, nameof(sourceOperationRunId));
        TransitionId = TraceabilityIdGuard.NotBlank(transitionId, nameof(transitionId));
        TargetOperationId = TraceabilityIdGuard.NotBlank(targetOperationId, nameof(targetOperationId));
        SourceJudgement = Enum.IsDefined(sourceJudgement)
            ? sourceJudgement
            : throw new ArgumentOutOfRangeException(nameof(sourceJudgement));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(traversal);
        Traversal = traversal;
        DecidedAtUtc = decidedAtUtc == default
            ? throw new ArgumentException("Route decision timestamp is required.", nameof(decidedAtUtc))
            : decidedAtUtc;
    }

    public string SourceOperationRunId { get; }
    public string TransitionId { get; }
    public string TargetOperationId { get; }
    public ResultJudgement SourceJudgement { get; }
    public int Traversal { get; }
    public DateTimeOffset DecidedAtUtc { get; }
}
