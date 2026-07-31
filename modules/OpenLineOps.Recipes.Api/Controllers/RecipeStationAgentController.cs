using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Recipes.Api.Mapping;
using OpenLineOps.Recipes.Api.Models;
using OpenLineOps.Recipes.Application.Contracts;
using OpenLineOps.Recipes.Application.Services;
using OpenLineOps.Recipes.Domain.Deployments;
using OpenLineOps.Recipes.Domain.Recipes;

namespace OpenLineOps.Recipes.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Recipes)]
[Route("api/recipes/{recipeId}")]
[Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
public sealed class RecipeStationAgentController(
    IRecipeOperationsService recipeService) : ControllerBase
{
    [HttpPost("assignments/{assignmentId:guid}/deployments")]
    [ProducesResponseType<RecipeDeploymentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeDeploymentResponse>> CreateDeploymentAsync(
        string recipeId,
        Guid assignmentId,
        CreateRecipeDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var stationId = User.GetRequiredStationId();
        if (request.AssignmentId != assignmentId)
        {
            return RecipeApiMapper.ToProblem(ApplicationError.Validation(
                "Recipe.Deployment.AssignmentRouteMismatch",
                "Request assignment id must match the route."));
        }

        var assignment = await recipeService
            .GetAssignmentAsync(assignmentId, cancellationToken)
            .ConfigureAwait(false);
        if (assignment.IsFailure)
        {
            return RecipeApiMapper.ToProblem(assignment.Error);
        }

        if (!string.Equals(
                assignment.Value.StationId,
                stationId,
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        if (!string.Equals(
                assignment.Value.RecipeId,
                recipeId,
                StringComparison.Ordinal))
        {
            return NotFound();
        }

        CreateRecipeDeploymentCommand command;
        try
        {
            command = new CreateRecipeDeploymentCommand(
                request.DeploymentId,
                assignmentId,
                recipeId,
                stationId,
                new DeploymentCommand(
                    request.CommandId,
                    request.FencingToken,
                    request.DeadlineUtc),
                User.GetRequiredActorId());
        }
        catch (ArgumentException exception)
        {
            return RecipeApiMapper.ToProblem(ApplicationError.Validation(
                "Recipe.Deployment.CommandInvalid",
                exception.Message));
        }

        var result = await recipeService
            .CreateDeploymentAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return RecipeApiMapper.ToProblem(result.Error);
        }

        var response = RecipeApiMapper.ToResponse(result.Value);
        return Created(
            $"/api/recipes/{Uri.EscapeDataString(recipeId)}/verification/{response.DeploymentId:D}",
            response);
    }

    [HttpPost("verification")]
    [ProducesResponseType<RecipeDeploymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeDeploymentResponse>> RecordVerificationAsync(
        string recipeId,
        RecordRecipeVerificationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? commandId,
        CancellationToken cancellationToken)
    {
        RecipeReadbackParameter[] readback;
        try
        {
            readback = request.Readback.Select(parameter =>
            {
                if (!Enum.TryParse<RecipeParameterValueType>(
                        parameter.Type,
                        ignoreCase: false,
                        out var type)
                    || !Enum.IsDefined(type))
                {
                    throw new ArgumentException(
                        $"Readback parameter type '{parameter.Type}' is invalid.");
                }

                return new RecipeReadbackParameter(
                    parameter.Key,
                    type,
                    parameter.Unit,
                    parameter.Value);
            }).ToArray();
        }
        catch (ArgumentException exception)
        {
            return RecipeApiMapper.ToProblem(ApplicationError.Validation(
                "Recipe.Verification.ReadbackInvalid",
                exception.Message));
        }

        var deployment = await recipeService
            .GetDeploymentAsync(request.DeploymentId, cancellationToken)
            .ConfigureAwait(false);
        if (deployment.IsFailure)
        {
            return RecipeApiMapper.ToProblem(deployment.Error);
        }

        var stationId = User.GetRequiredStationId();
        if (!string.Equals(
                deployment.Value.StationId,
                stationId,
                StringComparison.Ordinal))
        {
            return Forbid();
        }

        if (!string.Equals(
                deployment.Value.RecipeId,
                recipeId,
                StringComparison.Ordinal))
        {
            return NotFound();
        }

        var result = await recipeService.RecordVerificationAsync(
                new RecordRecipeVerificationCommand(
                    request.DeploymentId,
                    request.VerificationId,
                    request.ExpectedRevision,
                    recipeId,
                    stationId,
                    readback,
                    request.VerifiedAtUtc,
                    User.GetRequiredActorId(),
                    commandId ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? RecipeApiMapper.ToProblem(result.Error)
            : Ok(RecipeApiMapper.ToResponse(result.Value));
    }
}
