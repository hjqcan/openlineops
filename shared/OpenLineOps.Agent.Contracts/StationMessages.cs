using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed record StationJobRecoveryRequired(
    Guid MessageId,
    string IdempotencyKey,
    Guid JobId,
    string JobIdempotencyKey,
    string AgentId,
    string StationId,
    Guid ProductionRunId,
    string OperationRunId,
    Guid RuntimeSessionId,
    string Reason,
    DateTimeOffset DetectedAtUtc);

public sealed record MaterialArrived(
    [property: JsonConverter(typeof(CanonicalLowercaseGuidJsonConverter))] Guid MessageId,
    string IdempotencyKey,
    string ProducerId,
    string StationId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string PackageContentSha256,
    string MaterialKind,
    string MaterialId,
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

public static class StationMaterialArrivalProducers
{
    public const string CoordinatorApi = "coordinator-api";
}

public sealed class CanonicalLowercaseGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : null;
        if (!Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new JsonException(
                "Material arrival MessageId must be one non-empty lowercase D-format UUID.");
        }

        return parsed;
    }

    public override void Write(
        Utf8JsonWriter writer,
        Guid value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D"));
}

public static class StationMaterialKinds
{
    public const string ProductionUnit = "ProductionUnit";
    public const string Carrier = "Carrier";

    public static bool IsDefined(string value) => value is ProductionUnit or Carrier;
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

    public static void Validate(StationJobRequested message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty
            || message.JobId == Guid.Empty
            || message.ProductionRunId == Guid.Empty
            || message.ProductionUnitId == Guid.Empty
            || message.RuntimeSessionId == Guid.Empty)
        {
            throw new InvalidDataException("Station job request identity is incomplete.");
        }

        _ = Required(message.IdempotencyKey, nameof(message.IdempotencyKey));
        _ = Required(message.AgentId, nameof(message.AgentId));
        _ = Required(message.StationId, nameof(message.StationId));
        _ = Required(message.StationSystemId, nameof(message.StationSystemId));
        _ = Required(message.OperationRunId, nameof(message.OperationRunId));
        _ = Required(message.ProductModelId, nameof(message.ProductModelId));
        _ = Required(message.ProductionUnitIdentityInputKey, nameof(message.ProductionUnitIdentityInputKey));
        _ = Required(message.ProductionUnitIdentityValue, nameof(message.ProductionUnitIdentityValue));
        _ = Required(message.ProjectId, nameof(message.ProjectId));
        _ = Required(message.ApplicationId, nameof(message.ApplicationId));
        _ = Required(message.ProjectSnapshotId, nameof(message.ProjectSnapshotId));
        _ = Required(message.ProductionLineDefinitionId, nameof(message.ProductionLineDefinitionId));
        _ = Required(message.TopologyId, nameof(message.TopologyId));
        _ = Required(message.ActorId, nameof(message.ActorId));
        _ = Required(message.PackageContentSha256, nameof(message.PackageContentSha256));
        _ = Required(message.OperationId, nameof(message.OperationId));
        _ = Required(message.FlowDefinitionId, nameof(message.FlowDefinitionId));
        _ = Required(message.FlowVersionId, nameof(message.FlowVersionId));
        _ = Required(message.ConfigurationSnapshotId, nameof(message.ConfigurationSnapshotId));
        _ = Required(message.RecipeSnapshotId, nameof(message.RecipeSnapshotId));
        if (message.OperationAttempt <= 0
            || message.PackageContentSha256.Length != 64
            || message.PackageContentSha256.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || message.Inputs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Station job request payload is invalid.");
        }

        RequireUtc(message.RequestedAtUtc, "Station job request");
        if (message.ResourceFences is null
            || message.ResourceFences.Count == 0
            || message.ResourceFences.Any(static fence => fence is null))
        {
            throw new InvalidDataException("Station job resource fences contain a null member.");
        }

        var keys = new HashSet<(string Kind, string Id)>();
        foreach (var fence in message.ResourceFences)
        {
            _ = Required(fence.ResourceId, nameof(fence.ResourceId));
            if (!ResourceKinds.Contains(fence.ResourceKind)
                || fence.FencingToken <= 0
                || !keys.Add((fence.ResourceKind, fence.ResourceId)))
            {
                throw new InvalidDataException("Station job resource fences are invalid or duplicated.");
            }

            RequireUtc(fence.ExpiresAtUtc, "Station job resource fence expiry");
            if (fence.ExpiresAtUtc <= message.RequestedAtUtc)
            {
                throw new InvalidDataException(
                    "Station job resource fence must expire after the request timestamp.");
            }
        }

        if (!message.ResourceFences.Any(fence =>
                string.Equals(fence.ResourceKind, "Station", StringComparison.Ordinal)
                && string.Equals(fence.ResourceId, message.StationSystemId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Station job request requires an exact Station fence for its Station System.");
        }
    }

    public static void Validate(StationJobAccepted message)
    {
        ValidateJobEventIdentity(
            message.MessageId,
            message.JobId,
            message.IdempotencyKey,
            message.AgentId,
            message.StationId,
            message.AcceptedAtUtc,
            "Station job acceptance");
    }

    public static void Validate(StationJobProgressed message)
    {
        ValidateJobEventIdentity(
            message.MessageId,
            message.JobId,
            message.IdempotencyKey,
            message.AgentId,
            message.StationId,
            message.ProgressedAtUtc,
            "Station job progress");
        _ = Required(message.Phase, nameof(message.Phase));
        if (message.Percent is < 0 or > 100)
        {
            throw new InvalidDataException("Station job progress percent is outside 0..100.");
        }
    }

    public static void Validate(StationJobCompleted message)
    {
        ValidateJobEventIdentity(
            message.MessageId,
            message.JobId,
            message.IdempotencyKey,
            message.AgentId,
            message.StationId,
            message.CompletedAtUtc,
            "Station job completion");
        if (message.RuntimeSessionId == Guid.Empty
            || !Enum.IsDefined(message.ExecutionStatus)
            || message.ExecutionStatus is ExecutionStatus.Pending or ExecutionStatus.Running
            || !Enum.IsDefined(message.Judgement)
            || message.Outputs.ValueKind != JsonValueKind.Object
            || message.Steps is null
            || message.Commands is null
            || message.Incidents is null
            || message.Artifacts is null
            || message.Steps.Any(static item => item is null)
            || message.Commands.Any(static item => item is null)
            || message.Incidents.Any(static item => item is null)
            || message.Artifacts.Any(static item => item is null))
        {
            throw new InvalidDataException("Station job completion payload is incomplete.");
        }

        if (message.CompletedStepCount != message.Steps.Count(static step =>
                string.Equals(step.Status, "Completed", StringComparison.Ordinal))
            || message.CommandCount != message.Commands.Count
            || message.IncidentCount != message.Incidents.Count
            || message.Steps.Select(static step => step.StepId).Distinct().Count() != message.Steps.Count
            || message.Commands.Select(static command => command.CommandId).Distinct().Count() != message.Commands.Count
            || message.Incidents.Select(static incident => incident.IncidentId).Distinct().Count() != message.Incidents.Count
            || message.Artifacts.Select(static artifact => artifact.StorageKey)
                .Distinct(StringComparer.Ordinal).Count() != message.Artifacts.Count)
        {
            throw new InvalidDataException(
                "Station job completion evidence identities or counts are inconsistent.");
        }

        var hasFailureCode = !string.IsNullOrWhiteSpace(message.FailureCode);
        var hasFailureReason = !string.IsNullOrWhiteSpace(message.FailureReason);
        var validOutcome = message.ExecutionStatus switch
        {
            ExecutionStatus.Completed => message.Judgement is ResultJudgement.Passed
                or ResultJudgement.Failed
                or ResultJudgement.NotApplicable
                && !hasFailureCode
                && !hasFailureReason,
            ExecutionStatus.Canceled => message.Judgement == ResultJudgement.Aborted
                && hasFailureCode
                && hasFailureReason,
            ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Rejected =>
                message.Judgement == ResultJudgement.Unknown
                && hasFailureCode
                && hasFailureReason,
            _ => false
        };
        if (!validOutcome)
        {
            throw new InvalidDataException(
                "Station job execution status, product judgement, and failure evidence are inconsistent.");
        }

        foreach (var step in message.Steps)
        {
            if (step.StepId == Guid.Empty)
            {
                throw new InvalidDataException("Station job step identity is empty.");
            }

            _ = Required(step.NodeId, nameof(step.NodeId));
            _ = Required(step.ActionId, nameof(step.ActionId));
            _ = Required(step.TargetKind, nameof(step.TargetKind));
            _ = Required(step.TargetId, nameof(step.TargetId));
            _ = Required(step.DisplayName, nameof(step.DisplayName));
            _ = Required(step.Status, nameof(step.Status));
            RequireUtc(step.StartedAtUtc, "Station job step start");
            RequireOptionalUtcAfter(step.CompletedAtUtc, step.StartedAtUtc, "Station job step completion");
            RequireNotAfter(step.StartedAtUtc, message.CompletedAtUtc, "Station job step start");
            RequireNotAfter(step.CompletedAtUtc, message.CompletedAtUtc, "Station job step completion");
        }

        foreach (var command in message.Commands)
        {
            if (command.CommandId == Guid.Empty || command.StepId == Guid.Empty)
            {
                throw new InvalidDataException("Station job command identity is empty.");
            }

            _ = Required(command.NodeId, nameof(command.NodeId));
            _ = Required(command.ActionId, nameof(command.ActionId));
            _ = Required(command.TargetKind, nameof(command.TargetKind));
            _ = Required(command.TargetId, nameof(command.TargetId));
            _ = Required(command.CapabilityId, nameof(command.CapabilityId));
            _ = Required(command.CommandName, nameof(command.CommandName));
            _ = Required(command.Status, nameof(command.Status));
            RequireUtc(command.CreatedAtUtc, "Station job command creation");
            RequireUtc(command.DeadlineAtUtc, "Station job command deadline");
            if (command.DeadlineAtUtc < command.CreatedAtUtc)
            {
                throw new InvalidDataException("Station job command deadline precedes creation.");
            }

            RequireOptionalUtcAfter(command.AcceptedAtUtc, command.CreatedAtUtc, "Station job command acceptance");
            RequireOptionalUtcAfter(
                command.StartedAtUtc,
                command.AcceptedAtUtc ?? command.CreatedAtUtc,
                "Station job command start");
            RequireOptionalUtcAfter(
                command.CompletedAtUtc,
                command.StartedAtUtc ?? command.AcceptedAtUtc ?? command.CreatedAtUtc,
                "Station job command completion");
            if (command.ResultJudgement is not null
                && !Enum.IsDefined(command.ResultJudgement.Value))
            {
                throw new InvalidDataException("Station job command judgement is invalid.");
            }

            RequireNotAfter(command.CreatedAtUtc, message.CompletedAtUtc, "Station job command creation");
            RequireNotAfter(command.AcceptedAtUtc, message.CompletedAtUtc, "Station job command acceptance");
            RequireNotAfter(command.StartedAtUtc, message.CompletedAtUtc, "Station job command start");
            RequireNotAfter(command.CompletedAtUtc, message.CompletedAtUtc, "Station job command completion");
        }

        foreach (var incident in message.Incidents)
        {
            if (incident.IncidentId == Guid.Empty)
            {
                throw new InvalidDataException("Station job incident identity is empty.");
            }

            _ = Required(incident.Severity, nameof(incident.Severity));
            _ = Required(incident.Code, nameof(incident.Code));
            _ = Required(incident.Message, nameof(incident.Message));
            RequireUtc(incident.OccurredAtUtc, "Station job incident");
            RequireNotAfter(incident.OccurredAtUtc, message.CompletedAtUtc, "Station job incident");
        }

        foreach (var artifact in message.Artifacts)
        {
            _ = Required(artifact.Name, nameof(artifact.Name));
            _ = Required(artifact.Kind, nameof(artifact.Kind));
            _ = Required(artifact.StorageKey, nameof(artifact.StorageKey));
            _ = Required(artifact.Sha256, nameof(artifact.Sha256));
            if (artifact.SizeBytes < 0
                || artifact.Sha256.Length != 64
                || artifact.Sha256.Any(static character =>
                    character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw new InvalidDataException("Station job artifact evidence is invalid.");
            }
        }
    }

    public static void Validate(StationJobRecoveryRequired message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty
            || message.JobId == Guid.Empty
            || message.ProductionRunId == Guid.Empty
            || message.RuntimeSessionId == Guid.Empty)
        {
            throw new InvalidDataException(
                "Station Job recovery-required identity is incomplete.");
        }

        _ = Required(message.IdempotencyKey, nameof(message.IdempotencyKey));
        _ = Required(message.JobIdempotencyKey, nameof(message.JobIdempotencyKey));
        _ = Required(message.AgentId, nameof(message.AgentId));
        _ = Required(message.StationId, nameof(message.StationId));
        _ = Required(message.OperationRunId, nameof(message.OperationRunId));
        _ = Required(message.Reason, nameof(message.Reason));
        RequireUtc(message.DetectedAtUtc, "Station Job recovery-required");
    }

    public static void Validate(MaterialArrived message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty)
        {
            throw new InvalidDataException("Material arrival identity is incomplete.");
        }

        _ = Required(message.IdempotencyKey, nameof(message.IdempotencyKey));
        _ = Required(message.ProducerId, nameof(message.ProducerId));
        _ = Required(message.StationId, nameof(message.StationId));
        _ = Required(message.ProjectId, nameof(message.ProjectId));
        _ = Required(message.ApplicationId, nameof(message.ApplicationId));
        _ = Required(message.ProjectSnapshotId, nameof(message.ProjectSnapshotId));
        _ = Required(message.PackageContentSha256, nameof(message.PackageContentSha256));
        _ = Required(message.MaterialId, nameof(message.MaterialId));
        _ = Required(message.LineId, nameof(message.LineId));
        _ = Required(message.StationSystemId, nameof(message.StationSystemId));
        _ = Required(message.ActorId, nameof(message.ActorId));
        if (message.PackageContentSha256.Length != 64
            || message.PackageContentSha256.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                "Material arrival package content SHA-256 must be lowercase hexadecimal.");
        }

        if (!StationMaterialArrivalSources.IsDefined(message.Source))
        {
            throw new InvalidDataException(
                $"Unsupported material arrival source '{message.Source}'.");
        }


        if (!StationMaterialKinds.IsDefined(message.MaterialKind))
        {
            throw new InvalidDataException(
                $"Unsupported Station material kind '{message.MaterialKind}'.");
        }

        if (message.MaterialKind == StationMaterialKinds.ProductionUnit
            && (!Guid.TryParseExact(message.MaterialId, "D", out var productionUnitId)
                || !string.Equals(
                    message.MaterialId,
                    productionUnitId.ToString("D"),
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Production Unit material id must be canonical lowercase D-format UUID text.");
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

    private static void RequireOptionalUtcAfter(
        DateTimeOffset? value,
        DateTimeOffset earliest,
        string description)
    {
        if (value is null)
        {
            return;
        }

        RequireUtc(value.Value, description);
        if (value < earliest)
        {
            throw new InvalidDataException($"{description} precedes its prerequisite event.");
        }
    }

    private static void RequireNotAfter(
        DateTimeOffset? value,
        DateTimeOffset latest,
        string description)
    {
        if (value is not null && value > latest)
        {
            throw new InvalidDataException($"{description} follows the terminal completion timestamp.");
        }
    }

    private static void ValidateJobEventIdentity(
        Guid messageId,
        Guid jobId,
        string idempotencyKey,
        string agentId,
        string stationId,
        DateTimeOffset occurredAtUtc,
        string description)
    {
        if (messageId == Guid.Empty || jobId == Guid.Empty)
        {
            throw new InvalidDataException($"{description} identity is incomplete.");
        }

        _ = Required(idempotencyKey, nameof(idempotencyKey));
        _ = Required(agentId, nameof(agentId));
        _ = Required(stationId, nameof(stationId));
        RequireUtc(occurredAtUtc, description);
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
