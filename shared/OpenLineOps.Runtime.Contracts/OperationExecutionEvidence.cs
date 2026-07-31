namespace OpenLineOps.Runtime.Contracts;

public enum OperationExecutionEvidenceOrigin
{
    RuntimeSession,
    StationAgent,
    Coordinator
}

public sealed record OperationExecutionEvidence
{
    public OperationExecutionEvidence(
        OperationExecutionEvidenceOrigin origin,
        Guid runtimeSessionId,
        Guid productionRunId,
        Guid productionUnitId,
        string productionLineDefinitionId,
        string operationId,
        string operationRunId,
        int operationAttempt,
        string stationSystemId,
        string stationId,
        string processDefinitionId,
        string processVersionId,
        string configurationSnapshotId,
        string recipeSnapshotId,
        string productModelId,
        string identityInputKey,
        string identityValue,
        string? lotId,
        string? carrierId,
        string? fixtureId,
        string? deviceId,
        string actorId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string runtimeSessionStatus,
        DateTimeOffset completedAtUtc,
        IReadOnlyList<OperationResourceFenceEvidence> resourceFences,
        IReadOnlyList<OperationStepExecutionEvidence> steps,
        IReadOnlyList<OperationCommandExecutionEvidence> commands,
        IReadOnlyList<OperationIncidentExecutionEvidence> incidents,
        IReadOnlyList<OperationArtifactExecutionEvidence> artifacts)
    {
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        if (runtimeSessionId == Guid.Empty
            || productionRunId == Guid.Empty
            || productionUnitId == Guid.Empty)
        {
            throw new ArgumentException("Operation evidence identities cannot be empty.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationAttempt);
        RequireUtc(completedAtUtc, nameof(completedAtUtc));
        Origin = origin;
        RuntimeSessionId = runtimeSessionId;
        ProductionRunId = productionRunId;
        ProductionUnitId = productionUnitId;
        ProductionLineDefinitionId = Required(productionLineDefinitionId, nameof(productionLineDefinitionId));
        OperationId = Required(operationId, nameof(operationId));
        OperationRunId = Required(operationRunId, nameof(operationRunId));
        OperationAttempt = operationAttempt;
        if (!string.Equals(
                OperationRunId,
                $"{OperationId}@{OperationAttempt:D4}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Operation evidence Run id must match its Operation and attempt.",
                nameof(operationRunId));
        }

        StationSystemId = Required(stationSystemId, nameof(stationSystemId));
        StationId = Required(stationId, nameof(stationId));
        ProcessDefinitionId = Required(processDefinitionId, nameof(processDefinitionId));
        ProcessVersionId = Required(processVersionId, nameof(processVersionId));
        ConfigurationSnapshotId = Required(configurationSnapshotId, nameof(configurationSnapshotId));
        RecipeSnapshotId = Required(recipeSnapshotId, nameof(recipeSnapshotId));
        ProductModelId = Required(productModelId, nameof(productModelId));
        IdentityInputKey = Required(identityInputKey, nameof(identityInputKey));
        IdentityValue = Required(identityValue, nameof(identityValue));
        LotId = Optional(lotId, nameof(lotId));
        CarrierId = Optional(carrierId, nameof(carrierId));
        FixtureId = Optional(fixtureId, nameof(fixtureId));
        DeviceId = Optional(deviceId, nameof(deviceId));
        ActorId = Required(actorId, nameof(actorId));
        ProjectId = Required(projectId, nameof(projectId));
        ApplicationId = Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = Required(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = Required(topologyId, nameof(topologyId));
        RuntimeSessionStatus = Required(runtimeSessionStatus, nameof(runtimeSessionStatus));
        if (RuntimeSessionStatus is not "Completed" and not "Failed"
            and not "Canceled" and not "Stopped")
        {
            throw new ArgumentException(
                "Operation evidence Runtime Session status must be terminal and canonical.",
                nameof(runtimeSessionStatus));
        }

        CompletedAtUtc = completedAtUtc;
        ResourceFences = Canonicalize(
            resourceFences,
            static item => $"{item.ResourceKind}\u001f{item.ResourceId}",
            nameof(resourceFences));
        Steps = Canonicalize(steps, static item => item.StepId.ToString("D"), nameof(steps));
        Commands = Canonicalize(commands, static item => item.CommandId.ToString("D"), nameof(commands));
        Incidents = Canonicalize(incidents, static item => item.IncidentId.ToString("D"), nameof(incidents));
        Artifacts = Canonicalize(artifacts, static item => item.StorageKey, nameof(artifacts));
        var stepsById = Steps.ToDictionary(static step => step.StepId);
        if (Steps.Any(step => step.CompletedAtUtc > CompletedAtUtc)
            || Commands.Any(command =>
                !stepsById.TryGetValue(command.StepId, out var step)
                || !string.Equals(command.NodeId, step.NodeId, StringComparison.Ordinal)
                || !string.Equals(command.ActionId, step.ActionId, StringComparison.Ordinal)
                || !string.Equals(command.TargetKind, step.TargetKind, StringComparison.Ordinal)
                || !string.Equals(command.TargetId, step.TargetId, StringComparison.Ordinal)
                || command.CompletedAtUtc > CompletedAtUtc)
            || Incidents.Any(incident => incident.OccurredAtUtc > CompletedAtUtc)
            || Origin == OperationExecutionEvidenceOrigin.RuntimeSession && Artifacts.Count != 0
            || Origin == OperationExecutionEvidenceOrigin.Coordinator
                && (Steps.Count != 0 || Commands.Count != 0 || Artifacts.Count != 0))
        {
            throw new ArgumentException(
                "Operation evidence detail is inconsistent with its source or completion boundary.");
        }
    }

    public OperationExecutionEvidenceOrigin Origin { get; }
    public Guid RuntimeSessionId { get; }
    public Guid ProductionRunId { get; }
    public Guid ProductionUnitId { get; }
    public string ProductionLineDefinitionId { get; }
    public string OperationId { get; }
    public string OperationRunId { get; }
    public int OperationAttempt { get; }
    public string StationSystemId { get; }
    public string StationId { get; }
    public string ProcessDefinitionId { get; }
    public string ProcessVersionId { get; }
    public string ConfigurationSnapshotId { get; }
    public string RecipeSnapshotId { get; }
    public string ProductModelId { get; }
    public string IdentityInputKey { get; }
    public string IdentityValue { get; }
    public string? LotId { get; }
    public string? CarrierId { get; }
    public string? FixtureId { get; }
    public string? DeviceId { get; }
    public string ActorId { get; }
    public string ProjectId { get; }
    public string ApplicationId { get; }
    public string ProjectSnapshotId { get; }
    public string TopologyId { get; }
    public string RuntimeSessionStatus { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public IReadOnlyList<OperationResourceFenceEvidence> ResourceFences { get; }
    public IReadOnlyList<OperationStepExecutionEvidence> Steps { get; }
    public IReadOnlyList<OperationCommandExecutionEvidence> Commands { get; }
    public IReadOnlyList<OperationIncidentExecutionEvidence> Incidents { get; }
    public IReadOnlyList<OperationArtifactExecutionEvidence> Artifacts { get; }

    private static T[] Canonicalize<T>(
        IReadOnlyCollection<T> values,
        Func<T, string> key,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.ToArray();
        if (items.Any(static item => item is null)
            || items.Select(key).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw new ArgumentException(
                $"{parameterName} must contain unique non-null evidence.",
                parameterName);
        }

        return items.OrderBy(key, StringComparer.Ordinal).ToArray();
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;

    private static string? Optional(string? value, string parameterName) =>
        value is null ? null : Required(value, parameterName);

    internal static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{parameterName} must be a non-default UTC timestamp.",
                parameterName);
        }
    }
}

public sealed record OperationResourceFenceEvidence
{
    public OperationResourceFenceEvidence(
        string resourceKind,
        string resourceId,
        long fencingToken,
        DateTimeOffset expiresAtUtc)
    {
        ResourceKind = Required(resourceKind, nameof(resourceKind));
        ResourceId = Required(resourceId, nameof(resourceId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fencingToken);
        OperationExecutionEvidence.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        FencingToken = fencingToken;
        ExpiresAtUtc = expiresAtUtc;
    }

    public string ResourceKind { get; }
    public string ResourceId { get; }
    public long FencingToken { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) || value != value.Trim()
            ? throw new ArgumentException("Resource evidence text must be canonical.", parameterName)
            : value;
}

public sealed record OperationStepExecutionEvidence
{
    public OperationStepExecutionEvidence(
        Guid stepId,
        string nodeId,
        string actionId,
        string targetKind,
        string targetId,
        string displayName,
        string status,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? failureReason)
    {
        if (stepId == Guid.Empty)
        {
            throw new ArgumentException("Step evidence id cannot be empty.", nameof(stepId));
        }

        if (status is not "Completed" and not "Failed" and not "Skipped" and not "Canceled")
        {
            throw new ArgumentException("Step evidence status must be terminal and canonical.", nameof(status));
        }

        OperationExecutionEvidence.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        if (completedAtUtc is null || completedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("Step evidence requires ordered terminal timestamps.", nameof(completedAtUtc));
        }

        OperationExecutionEvidence.RequireUtc(completedAtUtc.Value, nameof(completedAtUtc));
        StepId = stepId;
        NodeId = EvidenceText.Required(nodeId, nameof(nodeId));
        ActionId = EvidenceText.Required(actionId, nameof(actionId));
        TargetKind = EvidenceText.Required(targetKind, nameof(targetKind));
        TargetId = EvidenceText.Required(targetId, nameof(targetId));
        DisplayName = EvidenceText.Required(displayName, nameof(displayName));
        Status = status;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        FailureReason = EvidenceText.Optional(failureReason, nameof(failureReason));
        if (Status == "Failed" && FailureReason is null
            || Status is "Completed" or "Skipped" && FailureReason is not null)
        {
            throw new ArgumentException(
                "Step evidence status and failure reason are inconsistent.",
                nameof(failureReason));
        }
    }

    public Guid StepId { get; }
    public string NodeId { get; }
    public string ActionId { get; }
    public string TargetKind { get; }
    public string TargetId { get; }
    public string DisplayName { get; }
    public string Status { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public string? FailureReason { get; }
}

public sealed record OperationCommandExecutionEvidence
{
    public OperationCommandExecutionEvidence(
        Guid commandId,
        Guid stepId,
        string nodeId,
        string actionId,
        string targetKind,
        string targetId,
        string capabilityId,
        string commandName,
        ExecutionStatus executionStatus,
        DateTimeOffset createdAtUtc,
        DateTimeOffset deadlineAtUtc,
        DateTimeOffset? acceptedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? resultPayload,
        string? failureReason,
        ResultJudgement? resultJudgement)
    {
        if (commandId == Guid.Empty || stepId == Guid.Empty)
        {
            throw new ArgumentException("Command and Step evidence ids cannot be empty.");
        }

        if (executionStatus is ExecutionStatus.Pending or ExecutionStatus.Running
            || !Enum.IsDefined(executionStatus))
        {
            throw new ArgumentException(
                "Command evidence execution status must be terminal and canonical.",
                nameof(executionStatus));
        }

        OperationExecutionEvidence.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        OperationExecutionEvidence.RequireUtc(deadlineAtUtc, nameof(deadlineAtUtc));
        if (deadlineAtUtc < createdAtUtc
            || completedAtUtc is null
            || acceptedAtUtc is not null && acceptedAtUtc < createdAtUtc
            || startedAtUtc is not null && startedAtUtc < (acceptedAtUtc ?? createdAtUtc)
            || completedAtUtc < (startedAtUtc ?? acceptedAtUtc ?? createdAtUtc))
        {
            throw new ArgumentException("Command evidence timestamps are not ordered.");
        }

        if (acceptedAtUtc is not null)
        {
            OperationExecutionEvidence.RequireUtc(acceptedAtUtc.Value, nameof(acceptedAtUtc));
        }

        if (startedAtUtc is not null)
        {
            OperationExecutionEvidence.RequireUtc(startedAtUtc.Value, nameof(startedAtUtc));
        }

        OperationExecutionEvidence.RequireUtc(completedAtUtc.Value, nameof(completedAtUtc));
        if (resultJudgement is null
            || executionStatus == ExecutionStatus.Completed
                && resultJudgement == OpenLineOps.Runtime.Contracts.ResultJudgement.Unknown
            || executionStatus == ExecutionStatus.Canceled
                && resultJudgement != OpenLineOps.Runtime.Contracts.ResultJudgement.Aborted
            || executionStatus is ExecutionStatus.Failed
                or ExecutionStatus.TimedOut
                or ExecutionStatus.Rejected
                && resultJudgement != OpenLineOps.Runtime.Contracts.ResultJudgement.Unknown)
        {
            throw new ArgumentException(
                "Command evidence status and judgement are inconsistent.",
                nameof(resultJudgement));
        }

        var canonicalFailure = EvidenceText.Optional(failureReason, nameof(failureReason));
        if (executionStatus == ExecutionStatus.Completed && canonicalFailure is not null
            || executionStatus != ExecutionStatus.Completed && canonicalFailure is null
            || executionStatus is ExecutionStatus.Completed
                or ExecutionStatus.Failed
                or ExecutionStatus.TimedOut
                && (acceptedAtUtc is null || startedAtUtc is null))
        {
            throw new ArgumentException(
                "Command evidence lifecycle fields do not match its terminal status.");
        }

        CommandId = commandId;
        StepId = stepId;
        NodeId = EvidenceText.Required(nodeId, nameof(nodeId));
        ActionId = EvidenceText.Required(actionId, nameof(actionId));
        TargetKind = EvidenceText.Required(targetKind, nameof(targetKind));
        TargetId = EvidenceText.Required(targetId, nameof(targetId));
        CapabilityId = EvidenceText.Required(capabilityId, nameof(capabilityId));
        CommandName = EvidenceText.Required(commandName, nameof(commandName));
        ExecutionStatus = executionStatus;
        CreatedAtUtc = createdAtUtc;
        DeadlineAtUtc = deadlineAtUtc;
        AcceptedAtUtc = acceptedAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        ResultPayload = resultPayload;
        FailureReason = canonicalFailure;
        ResultJudgement = resultJudgement;
    }

    public Guid CommandId { get; }
    public Guid StepId { get; }
    public string NodeId { get; }
    public string ActionId { get; }
    public string TargetKind { get; }
    public string TargetId { get; }
    public string CapabilityId { get; }
    public string CommandName { get; }
    public ExecutionStatus ExecutionStatus { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset DeadlineAtUtc { get; }
    public DateTimeOffset? AcceptedAtUtc { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public string? ResultPayload { get; }
    public string? FailureReason { get; }
    public ResultJudgement? ResultJudgement { get; }
}

public sealed record OperationIncidentExecutionEvidence
{
    public OperationIncidentExecutionEvidence(
        Guid incidentId,
        string severity,
        string code,
        string message,
        DateTimeOffset occurredAtUtc)
    {
        if (incidentId == Guid.Empty)
        {
            throw new ArgumentException("Incident evidence id cannot be empty.", nameof(incidentId));
        }

        if (severity is not "Information" and not "Warning" and not "Error" and not "Critical")
        {
            throw new ArgumentException("Incident evidence severity is not canonical.", nameof(severity));
        }

        OperationExecutionEvidence.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        IncidentId = incidentId;
        Severity = severity;
        Code = EvidenceText.Required(code, nameof(code));
        Message = EvidenceText.Required(message, nameof(message));
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid IncidentId { get; }
    public string Severity { get; }
    public string Code { get; }
    public string Message { get; }
    public DateTimeOffset OccurredAtUtc { get; }
}

public sealed record OperationArtifactExecutionEvidence
{
    public OperationArtifactExecutionEvidence(
        string name,
        string kind,
        string storageKey,
        string? mediaType,
        long sizeBytes,
        string sha256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        if (sha256.Length != 64
            || sha256.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Artifact SHA-256 must be canonical lowercase hex.", nameof(sha256));
        }

        Name = EvidenceText.Required(name, nameof(name));
        Kind = EvidenceText.Required(kind, nameof(kind));
        StorageKey = EvidenceText.Required(storageKey, nameof(storageKey));
        MediaType = EvidenceText.Optional(mediaType, nameof(mediaType));
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    public string Name { get; }
    public string Kind { get; }
    public string StorageKey { get; }
    public string? MediaType { get; }
    public long SizeBytes { get; }
    public string Sha256 { get; }
}

internal static class EvidenceText
{
    public static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) || value != value.Trim()
            ? throw new ArgumentException("Execution evidence text must be canonical.", parameterName)
            : value;

    public static string? Optional(string? value, string parameterName) =>
        value is null ? null : Required(value, parameterName);
}
