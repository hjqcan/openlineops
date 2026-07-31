using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Recipes.Api.Mapping;
using OpenLineOps.Recipes.Api.Models;
using OpenLineOps.Recipes.Application.Contracts;
using OpenLineOps.Recipes.Application.Services;

namespace OpenLineOps.Recipes.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Recipes)]
[Route("api/recipes/{recipeId}")]
[Authorize(Policy = OpenLineOpsApiSecurity.EngineeringPolicy)]
public sealed class RecipeEngineeringController(
    IRecipeOperationsService recipeService) : ControllerBase
{
    [HttpGet("revisions/{versionId}")]
    [ProducesResponseType<RecipeRevisionSnapshotResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecipeRevisionSnapshotResponse>> GetRevisionAsync(
        string recipeId,
        string versionId,
        CancellationToken cancellationToken)
    {
        var result = await recipeService.GetReleasedRevisionAsync(
                recipeId,
                versionId,
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? RecipeApiMapper.ToProblem(result.Error)
            : Ok(RecipeApiMapper.ToResponse(result.Value));
    }

    [HttpPost("assignments")]
    [ProducesResponseType<RecipeAssignmentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeAssignmentResponse>> CreateAssignmentAsync(
        string recipeId,
        CreateRecipeAssignmentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? commandId,
        CancellationToken cancellationToken)
    {
        var result = await recipeService.CreateAssignmentAsync(
                new CreateRecipeAssignmentCommand(
                    request.AssignmentId,
                    recipeId,
                    request.VersionId,
                    request.ProductModelId,
                    request.StationId,
                    request.EffectiveFromUtc,
                    request.EffectiveUntilUtc,
                    User.GetRequiredActorId(),
                    commandId ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return RecipeApiMapper.ToProblem(result.Error);
        }

        var response = RecipeApiMapper.ToResponse(result.Value);
        return Created(
            $"/api/recipes/{Uri.EscapeDataString(recipeId)}/assignments/{response.AssignmentId:D}",
            response);
    }

    [HttpGet("assignments")]
    [ProducesResponseType<IReadOnlyCollection<RecipeAssignmentResponse>>(
        StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<RecipeAssignmentResponse>>>
        ListAssignmentsAsync(
            string recipeId,
            [FromQuery] string? stationId,
            [FromQuery] string? productModelId,
            CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(static key =>
                key is not ("stationId" or "productModelId")))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation.StrictQuery",
                Detail = "Only stationId and productModelId query fields are supported."
            });
        }

        var result = await recipeService.ListAssignmentsAsync(
                recipeId,
                stationId,
                productModelId,
                cancellationToken)
            .ConfigureAwait(false);
        return Ok(result.Value.Select(RecipeApiMapper.ToResponse).ToArray());
    }

    [HttpGet("assignments/{assignmentId:guid}")]
    [ProducesResponseType<RecipeAssignmentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecipeAssignmentResponse>> GetAssignmentAsync(
        string recipeId,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        var result = await recipeService
            .GetAssignmentAsync(assignmentId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return RecipeApiMapper.ToProblem(result.Error);
        }

        return string.Equals(result.Value.RecipeId, recipeId, StringComparison.Ordinal)
            ? Ok(RecipeApiMapper.ToResponse(result.Value))
            : NotFound();
    }

    [HttpGet("verification/{deploymentId:guid}")]
    [ProducesResponseType<RecipeDeploymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecipeDeploymentResponse>> GetVerificationAsync(
        string recipeId,
        Guid deploymentId,
        CancellationToken cancellationToken)
    {
        var result = await recipeService
            .GetDeploymentAsync(deploymentId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return RecipeApiMapper.ToProblem(result.Error);
        }

        return string.Equals(result.Value.RecipeId, recipeId, StringComparison.Ordinal)
            ? Ok(RecipeApiMapper.ToResponse(result.Value))
            : NotFound();
    }
}
