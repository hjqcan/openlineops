using System.Text.Json;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent.Contracts;

public sealed record StationJobRequested(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    Guid ProductionUnitId,
    Guid RuntimeSessionId,
    string OperationRunId,
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
    IReadOnlyCollection<StationResourceFence> ResourceFences,
    JsonElement Inputs,
    DateTimeOffset RequestedAtUtc);

public sealed record StationResourceFence(
    string ResourceKind,
    string ResourceId,
    long FencingToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record StationJobAccepted(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    DateTimeOffset AcceptedAtUtc);

public sealed record StationJobProgressed(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    int Percent,
    string Phase,
    DateTimeOffset ProgressedAtUtc);

public sealed record StationJobArtifact(
    string Name,
    string Kind,
    string StorageKey,
    string? MediaType,
    long SizeBytes,
    string Sha256);

public sealed record StationJobStepEvidence(
    Guid StepId,
    string NodeId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string DisplayName,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureReason);

public sealed record StationJobCommandEvidence(
    Guid CommandId,
    Guid StepId,
    string NodeId,
    string ActionId,
    string TargetKind,
    string TargetId,
    string CapabilityId,
    string CommandName,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadlineAtUtc,
    DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultPayload,
    string? FailureReason,
    ResultJudgement? ResultJudgement);

public sealed record StationJobIncidentEvidence(
    Guid IncidentId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc);

public sealed record StationJobCompleted(
    Guid MessageId,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    Guid RuntimeSessionId,
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    JsonElement Outputs,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyCollection<StationJobStepEvidence> Steps,
    IReadOnlyCollection<StationJobCommandEvidence> Commands,
    IReadOnlyCollection<StationJobIncidentEvidence> Incidents,
    IReadOnlyCollection<StationJobArtifact> Artifacts,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset CompletedAtUtc);

public sealed record MaterialArrived(
    Guid MessageId,
    string IdempotencyKey,
    string ProducerId,
    string StationId,
    Guid ProductionUnitId,
    string LineId,
    string StationSystemId,
    string Source,
    string ActorId,
    DateTimeOffset ArrivedAtUtc);

public sealed record ResourceLeaseChanged(
    Guid MessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    Guid JobId,
    Guid ProductionRunId,
    string OperationRunId,
    string ResourceKind,
    string ResourceId,
    long FencingToken,
    string Status,
    DateTimeOffset ChangedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public static class StationMaterialArrivalSources
{
    public const string Api = "Api";
    public const string Manual = "Manual";
    public const string Plc = "Plc";

    public static bool IsDefined(string value) => value is Api or Manual or Plc;
}

public static class StationResourceLeaseStatuses
{
    public const string Granted = "Granted";
}

public static class StationMessageContract
{
    private static readonly HashSet<string> ResourceKinds = new(StringComparer.Ordinal)
    {
        "Station",
        "Slot",
        "Fixture",
        "Device",
        "SlotGroup"
    };

    public static void Validate(MaterialArrived message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty || message.ProductionUnitId == Guid.Empty)
        {
            throw new InvalidDataException("Material arrival identity is incomplete.");
        }

        _ = Required(message.IdempotencyKey, nameof(message.IdempotencyKey));
        _ = Required(message.ProducerId, nameof(message.ProducerId));
        _ = Required(message.StationId, nameof(message.StationId));
        _ = Required(message.LineId, nameof(message.LineId));
        _ = Required(message.StationSystemId, nameof(message.StationSystemId));
        _ = Required(message.ActorId, nameof(message.ActorId));
        if (!StationMaterialArrivalSources.IsDefined(message.Source))
        {
            throw new InvalidDataException(
                $"Unsupported material arrival source '{message.Source}'.");
        }

        RequireUtc(message.ArrivedAtUtc, "Material arrival");
    }

    public static void Validate(ResourceLeaseChanged message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty
            || message.JobId == Guid.Empty
            || message.ProductionRunId == Guid.Empty)
        {
            throw new InvalidDataException("Resource lease change identity is incomplete.");
        }

        _ = Required(message.IdempotencyKey, nameof(message.IdempotencyKey));
        _ = Required(message.AgentId, nameof(message.AgentId));
        _ = Required(message.StationId, nameof(message.StationId));
        _ = Required(message.OperationRunId, nameof(message.OperationRunId));
        _ = Required(message.ResourceId, nameof(message.ResourceId));
        if (!ResourceKinds.Contains(message.ResourceKind))
        {
            throw new InvalidDataException(
                $"Unsupported resource lease kind '{message.ResourceKind}'.");
        }

        if (!string.Equals(
                message.Status,
                StationResourceLeaseStatuses.Granted,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported resource lease status '{message.Status}'.");
        }

        if (message.FencingToken <= 0)
        {
            throw new InvalidDataException("Resource lease fencing token must be positive.");
        }

        RequireUtc(message.ChangedAtUtc, "Resource lease change");
        RequireUtc(message.ExpiresAtUtc, "Resource lease expiry");
        if (message.ExpiresAtUtc <= message.ChangedAtUtc)
        {
            throw new InvalidDataException(
                "Resource lease expiry must follow the change timestamp.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new InvalidDataException(
                $"{parameterName} must be canonical non-empty text.")
            : value;

    private static void RequireUtc(DateTimeOffset value, string description)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"{description} timestamp must be a non-default UTC value.");
        }
    }
}

public sealed record EmergencyStopRequested(
    Guid MessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string Reason,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc);

public sealed record EmergencyStopAcknowledged(
    Guid MessageId,
    Guid RequestMessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    bool Accepted,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset AcknowledgedAtUtc);

public sealed record StationSafeStopRequested(
    Guid MessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    string? OperationRunId,
    string ActorId,
    string Reason,
    DateTimeOffset RequestedAtUtc);

public sealed record StationSafeStopAcknowledged(
    Guid MessageId,
    Guid RequestMessageId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    bool Accepted,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset AcknowledgedAtUtc);

public sealed record StationJobCancelRequested(
    Guid MessageId,
    string IdempotencyKey,
    Guid JobId,
    string JobIdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    string OperationRunId,
    string ActorId,
    string Reason,
    DateTimeOffset RequestedAtUtc);

public sealed record StationJobCancelAcknowledged(
    Guid MessageId,
    Guid RequestMessageId,
    string IdempotencyKey,
    Guid JobId,
    string AgentId,
    string StationId,
    bool Accepted,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset AcknowledgedAtUtc);
