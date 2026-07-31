using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Recipes.Application.Contracts;
using OpenLineOps.Recipes.Domain.Assignments;
using OpenLineOps.Recipes.Domain.Changeovers;
using OpenLineOps.Recipes.Domain.Deployments;
using OpenLineOps.Recipes.Domain.Recipes;

namespace OpenLineOps.Recipes.Application.Services;

public interface IRecipeOperationsService
{
    ValueTask<Result<RecipeRevisionSnapshot>> GetReleasedRevisionAsync(
        string recipeId,
        string versionId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeAssignment>> CreateAssignmentAsync(
        CreateRecipeAssignmentCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeAssignment>> GetAssignmentAsync(
        Guid assignmentId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<IReadOnlyCollection<RecipeAssignment>>> ListAssignmentsAsync(
        string? recipeId = null,
        string? stationId = null,
        string? productModelId = null,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeDeployment>> CreateDeploymentAsync(
        CreateRecipeDeploymentCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeDeployment>> RecordVerificationAsync(
        RecordRecipeVerificationCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeDeployment>> GetDeploymentAsync(
        Guid deploymentId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeChangeover>> StartChangeoverAsync(
        StartRecipeChangeoverCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeChangeover>> AdvanceChangeoverAsync(
        AdvanceRecipeChangeoverCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeChangeover>> FailChangeoverAsync(
        TerminateRecipeChangeoverCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeChangeover>> CancelChangeoverAsync(
        TerminateRecipeChangeoverCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<Result<RecipeChangeover>> GetChangeoverAsync(
        Guid changeoverId,
        CancellationToken cancellationToken = default);
}
