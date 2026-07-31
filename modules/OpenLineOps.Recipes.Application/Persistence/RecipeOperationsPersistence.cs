using OpenLineOps.Recipes.Domain.Assignments;
using OpenLineOps.Recipes.Domain.Changeovers;
using OpenLineOps.Recipes.Domain.Deployments;

namespace OpenLineOps.Recipes.Application.Persistence;

public sealed record RecipeIdempotencyContext(
    string Scope,
    string CommandId,
    string RequestSha256);

public enum RecipeStoreWriteStatus
{
    Stored = 0,
    Replay = 1,
    IdempotencyConflict = 2,
    RevisionConflict = 3,
    AssignmentOverlap = 4,
    StaleFencingToken = 5
}

public sealed record RecipeStoreWriteResult<T>(
    RecipeStoreWriteStatus Status,
    T? Value)
    where T : class;

public enum RecipeCommandCheckStatus
{
    Missing = 0,
    Replay = 1,
    Conflict = 2
}

public sealed record RecipeCommandCheck<T>(
    RecipeCommandCheckStatus Status,
    T? Value)
    where T : class;

public sealed record RecipeFactMetadata(
    string StreamKind,
    Guid StreamId,
    long Revision,
    string FactKind,
    string StationId,
    DateTimeOffset OccurredAtUtc,
    string PayloadSha256,
    string PreviousFactSha256,
    string FactSha256);

public interface IRecipeOperationsStore
{
    ValueTask<RecipeCommandCheck<T>> CheckCommandAsync<T>(
        RecipeIdempotencyContext idempotency,
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken = default)
        where T : class;

    ValueTask<RecipeStoreWriteResult<RecipeAssignment>> CreateAssignmentAsync(
        RecipeAssignment assignment,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<RecipeAssignment?> GetAssignmentAsync(
        Guid assignmentId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RecipeAssignment>> ListAssignmentsAsync(
        string? recipeId = null,
        string? stationId = null,
        string? productModelId = null,
        CancellationToken cancellationToken = default);

    ValueTask<RecipeStoreWriteResult<RecipeDeployment>> CreateDeploymentAsync(
        RecipeDeployment deployment,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<RecipeStoreWriteResult<RecipeDeployment>> AppendVerificationAsync(
        RecipeDeployment deployment,
        long expectedRevision,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<RecipeDeployment?> GetDeploymentAsync(
        Guid deploymentId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RecipeDeployment>> ListDeploymentsAsync(
        string? stationId = null,
        string? productModelId = null,
        CancellationToken cancellationToken = default);

    ValueTask<RecipeStoreWriteResult<RecipeChangeover>> CreateChangeoverAsync(
        RecipeChangeover changeover,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<RecipeStoreWriteResult<RecipeChangeover>> AppendChangeoverAsync(
        RecipeChangeover changeover,
        long expectedRevision,
        RecipeIdempotencyContext idempotency,
        CancellationToken cancellationToken = default);

    ValueTask<RecipeChangeover?> GetChangeoverAsync(
        Guid changeoverId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RecipeChangeover>> ListChangeoversAsync(
        string? stationId = null,
        Guid? assignmentId = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RecipeFactMetadata>> ListFactsAsync(
        string streamKind,
        Guid streamId,
        CancellationToken cancellationToken = default);
}
