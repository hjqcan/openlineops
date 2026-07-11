using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.StationRuntime.Contracts;

public static class StationOperationDocumentContract
{
    public const string RequestSchema = "openlineops.station-operation-request";

    public const string ResultSchema = "openlineops.station-operation-result";

    public const string ResourceFenceValidationRequestSchema =
        "openlineops.station-resource-fence-validation-request";

    public const string ResourceFenceValidationResponseSchema =
        "openlineops.station-resource-fence-validation-response";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationOperationRequestDocument(
    string Schema,
    Guid JobId,
    string IdempotencyKey,
    string AgentId,
    string StationId,
    string StationSystemId,
    Guid ProductionRunId,
    Guid ProductionUnitId,
    Guid RuntimeSessionId,
    string ProductionLineDefinitionId,
    string TopologyId,
    string ActorId,
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
    string PackageContentSha256,
    string PackageContentDirectory,
    string OperationId,
    string FlowDefinitionId,
    string FlowVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    StationResourceFenceAuthorityDescriptor ResourceFenceAuthority,
    IReadOnlyList<StationOperationResourceFence> ResourceFences,
    JsonElement Inputs,
    DateTimeOffset RequestedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationOperationResourceFence(
    string ResourceKind,
    string ResourceId,
    long FencingToken,
    DateTimeOffset ExpiresAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationResourceFenceAuthorityDescriptor(
    string PipeName,
    string AccessToken);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationResourceFenceValidationRequest(
    string Schema,
    string AccessToken,
    Guid JobId,
    Guid ProductionRunId,
    string OperationRunId,
    IReadOnlyList<StationOperationResourceFence> ResourceFences);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationResourceFenceValidationResponse(
    string Schema,
    bool Accepted,
    string? RejectionReason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationOperationResultDocument(
    string Schema,
    Guid JobId,
    Guid RuntimeSessionId,
    ExecutionStatus ExecutionStatus,
    ResultJudgement Judgement,
    JsonElement Outputs,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<StationOperationStepEvidence> Steps,
    IReadOnlyList<StationOperationCommandEvidence> Commands,
    IReadOnlyList<StationOperationIncidentEvidence> Incidents,
    IReadOnlyList<StationOperationArtifactEvidence> Artifacts,
    string? FailureCode,
    string? FailureReason);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationOperationStepEvidence(
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationOperationCommandEvidence(
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationOperationIncidentEvidence(
    Guid IncidentId,
    string Severity,
    string Code,
    string Message,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationOperationArtifactEvidence(
    string RelativePath,
    string Name,
    string Kind,
    string? MediaType,
    long SizeBytes,
    string Sha256);
