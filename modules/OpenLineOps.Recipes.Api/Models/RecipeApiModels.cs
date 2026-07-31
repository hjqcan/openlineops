using System.Text.Json.Serialization;

namespace OpenLineOps.Recipes.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateRecipeAssignmentRequest(
    Guid AssignmentId,
    string VersionId,
    string ProductModelId,
    string StationId,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateRecipeDeploymentRequest(
    Guid DeploymentId,
    Guid AssignmentId,
    string CommandId,
    long FencingToken,
    DateTimeOffset DeadlineUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordRecipeVerificationRequest(
    Guid DeploymentId,
    Guid VerificationId,
    long ExpectedRevision,
    DateTimeOffset VerifiedAtUtc,
    IReadOnlyCollection<RecipeReadbackParameterRequest> Readback);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecipeReadbackParameterRequest(
    string Key,
    string Type,
    string? Unit,
    string Value);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StartRecipeChangeoverRequest(
    Guid ChangeoverId,
    Guid AssignmentId,
    DateTimeOffset StartedAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdvanceRecipeChangeoverRequest(
    long ExpectedRevision,
    string TargetState,
    string Evidence,
    Guid? DeploymentId,
    int? WorkInProgressCount,
    bool? LineClearanceConfirmed,
    DateTimeOffset OccurredAtUtc);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TerminateRecipeChangeoverRequest(
    long ExpectedRevision,
    string Reason,
    DateTimeOffset OccurredAtUtc);

public sealed record RecipeParameterSnapshotResponse(
    string Key,
    string Type,
    string? Unit,
    string CanonicalValue,
    bool Required);

public sealed record RecipeRevisionSnapshotResponse(
    string RecipeId,
    string VersionId,
    string DisplayName,
    DateTimeOffset ReleasedAtUtc,
    string ConfigurationSha256,
    IReadOnlyCollection<RecipeParameterSnapshotResponse> Parameters);

public sealed record RecipeAssignmentResponse(
    Guid AssignmentId,
    long Revision,
    string RecipeId,
    string VersionId,
    string ProductModelId,
    string StationId,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    string ConfigurationSha256,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy);

public sealed record DeploymentCommandResponse(
    string CommandId,
    long FencingToken,
    DateTimeOffset DeadlineUtc);

public sealed record RecipeParameterVerificationResponse(
    string Key,
    bool TypeMatches,
    bool UnitMatches,
    bool ValueMatches,
    string ExpectedType,
    string ActualType,
    string? ExpectedUnit,
    string? ActualUnit,
    string ExpectedValue,
    string ActualValue);

public sealed record RecipeVerificationResponse(
    Guid VerificationId,
    DateTimeOffset VerifiedAtUtc,
    string VerifiedBy,
    bool Succeeded,
    string ReadbackSha256,
    IReadOnlyCollection<RecipeParameterVerificationResponse> Parameters);

public sealed record RecipeDeploymentResponse(
    Guid DeploymentId,
    Guid AssignmentId,
    long Revision,
    string RecipeId,
    string VersionId,
    string ProductModelId,
    string StationId,
    string Status,
    RecipeRevisionSnapshotResponse Snapshot,
    DeploymentCommandResponse Command,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    RecipeVerificationResponse? Verification);

public sealed record RecipeChangeoverAuditResponse(
    long Revision,
    string Action,
    string State,
    string Evidence,
    int? WorkInProgressCount,
    bool? LineClearanceConfirmed,
    DateTimeOffset OccurredAtUtc,
    string ActorId);

public sealed record RecipeChangeoverResponse(
    Guid ChangeoverId,
    Guid AssignmentId,
    long Revision,
    string RecipeId,
    string VersionId,
    string ProductModelId,
    string StationId,
    string State,
    Guid? DeploymentId,
    DateTimeOffset StartedAtUtc,
    string StartedBy,
    IReadOnlyCollection<RecipeChangeoverAuditResponse> Audit);

public sealed record RecipeProductionReadinessResponse(
    bool Allowed,
    Guid? AssignmentId,
    Guid? DeploymentId,
    string? ConfigurationSha256,
    IReadOnlyCollection<RecipeProductionReadinessBlockResponse> Blocks);

public sealed record RecipeProductionReadinessBlockResponse(
    string Code,
    string Detail);
