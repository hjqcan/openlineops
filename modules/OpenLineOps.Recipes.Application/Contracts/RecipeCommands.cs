using OpenLineOps.Recipes.Domain.Changeovers;
using OpenLineOps.Recipes.Domain.Deployments;

namespace OpenLineOps.Recipes.Application.Contracts;

public sealed record CreateRecipeAssignmentCommand(
    Guid AssignmentId,
    string RecipeId,
    string VersionId,
    string ProductModelId,
    string StationId,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveUntilUtc,
    string ActorId,
    string CommandId);

public sealed record CreateRecipeDeploymentCommand(
    Guid DeploymentId,
    Guid AssignmentId,
    string RecipeId,
    string StationId,
    DeploymentCommand Command,
    string ActorId);

public sealed record RecordRecipeVerificationCommand(
    Guid DeploymentId,
    Guid VerificationId,
    long ExpectedRevision,
    string RecipeId,
    string StationId,
    IReadOnlyCollection<RecipeReadbackParameter> Readback,
    DateTimeOffset VerifiedAtUtc,
    string ActorId,
    string CommandId);

public sealed record StartRecipeChangeoverCommand(
    Guid ChangeoverId,
    Guid AssignmentId,
    string StationId,
    DateTimeOffset StartedAtUtc,
    string ActorId,
    string CommandId);

public sealed record AdvanceRecipeChangeoverCommand(
    Guid ChangeoverId,
    long ExpectedRevision,
    RecipeChangeoverState TargetState,
    string Evidence,
    Guid? DeploymentId,
    int? WorkInProgressCount,
    bool? LineClearanceConfirmed,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    string CommandId);

public sealed record TerminateRecipeChangeoverCommand(
    Guid ChangeoverId,
    long ExpectedRevision,
    string Reason,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    string CommandId);
