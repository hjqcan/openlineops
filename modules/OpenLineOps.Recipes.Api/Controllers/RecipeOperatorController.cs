using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Recipes.Api.Mapping;
using OpenLineOps.Recipes.Api.Models;
using OpenLineOps.Recipes.Application.Contracts;
using OpenLineOps.Recipes.Application.Readiness;
using OpenLineOps.Recipes.Application.Services;
using OpenLineOps.Recipes.Domain.Changeovers;

namespace OpenLineOps.Recipes.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Recipes)]
[Route("api/recipes/changeovers")]
[Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class RecipeOperatorController(
    IRecipeOperationsService recipeService,
    IRecipeProductionReadinessGate readinessGate) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<RecipeChangeoverResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeChangeoverResponse>> StartAsync(
        StartRecipeChangeoverRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? commandId,
        CancellationToken cancellationToken)
    {
        var assignment = await recipeService
            .GetAssignmentAsync(request.AssignmentId, cancellationToken)
            .ConfigureAwait(false);
        if (assignment.IsFailure)
        {
            return RecipeApiMapper.ToProblem(assignment.Error);
        }

        if (!User.IsInRole(OpenLineOpsApiSecurity.EngineeringRole)
            && User.HasClaim(OpenLineOpsApiSecurity.StationIdClaim, assignment.Value.StationId)
                is false)
        {
            return Forbid();
        }

        var result = await recipeService.StartChangeoverAsync(
                new StartRecipeChangeoverCommand(
                    request.ChangeoverId,
                    request.AssignmentId,
                    assignment.Value.StationId,
                    request.StartedAtUtc,
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
            $"/api/recipes/changeovers/{response.ChangeoverId:D}",
            response);
    }

    [HttpPost("{changeoverId:guid}/transitions")]
    [ProducesResponseType<RecipeChangeoverResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RecipeChangeoverResponse>> AdvanceAsync(
        Guid changeoverId,
        AdvanceRecipeChangeoverRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? commandId,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RecipeChangeoverState>(
                request.TargetState,
                ignoreCase: false,
                out var targetState)
            || !Enum.IsDefined(targetState))
        {
            return RecipeApiMapper.ToProblem(ApplicationError.Validation(
                "Recipe.Changeover.State.Invalid",
                "Target state must be an exact defined state name."));
        }

        var authorization = await AuthorizeChangeoverAsync(
                changeoverId,
                cancellationToken)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return authorization;
        }

        var result = await recipeService.AdvanceChangeoverAsync(
                new AdvanceRecipeChangeoverCommand(
                    changeoverId,
                    request.ExpectedRevision,
                    targetState,
                    request.Evidence,
                    request.DeploymentId,
                    request.WorkInProgressCount,
                    request.LineClearanceConfirmed,
                    request.OccurredAtUtc,
                    User.GetRequiredActorId(),
                    commandId ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? RecipeApiMapper.ToProblem(result.Error)
            : Ok(RecipeApiMapper.ToResponse(result.Value));
    }

    [HttpPost("{changeoverId:guid}/failures")]
    public Task<ActionResult<RecipeChangeoverResponse>> FailAsync(
        Guid changeoverId,
        TerminateRecipeChangeoverRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? commandId,
        CancellationToken cancellationToken) =>
        TerminateAsync(
            changeoverId,
            request,
            commandId,
            fail: true,
            cancellationToken);

    [HttpPost("{changeoverId:guid}/cancellation")]
    public Task<ActionResult<RecipeChangeoverResponse>> CancelAsync(
        Guid changeoverId,
        TerminateRecipeChangeoverRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? commandId,
        CancellationToken cancellationToken) =>
        TerminateAsync(
            changeoverId,
            request,
            commandId,
            fail: false,
            cancellationToken);

    [HttpGet("{changeoverId:guid}")]
    [ProducesResponseType<RecipeChangeoverResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecipeChangeoverResponse>> GetAsync(
        Guid changeoverId,
        CancellationToken cancellationToken)
    {
        var changeover = await recipeService
            .GetChangeoverAsync(changeoverId, cancellationToken)
            .ConfigureAwait(false);
        if (changeover.IsFailure)
        {
            return RecipeApiMapper.ToProblem(changeover.Error);
        }

        return IsAuthorizedForStation(changeover.Value.StationId)
            ? Ok(RecipeApiMapper.ToResponse(changeover.Value))
            : Forbid();
    }

    [HttpGet("~/api/recipes/{recipeId}/verification")]
    [ProducesResponseType<RecipeProductionReadinessResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RecipeProductionReadinessResponse>> ReadinessAsync(
        string recipeId,
        [FromQuery] string stationId,
        [FromQuery] string productModelId,
        [FromQuery] string versionId,
        [FromQuery] DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (Request.Query.Keys.Any(static key =>
                key is not (
                    "stationId"
                    or "productModelId"
                    or "versionId"
                    or "evaluatedAtUtc")))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation.StrictQuery",
                Detail = "Readiness query contains an unsupported field."
            });
        }

        if (!IsAuthorizedForStation(stationId))
        {
            return Forbid();
        }

        var readiness = await readinessGate.EvaluateAsync(
                new RecipeProductionReadinessRequest(
                    stationId,
                    productModelId,
                    recipeId,
                    versionId,
                    evaluatedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        return Ok(RecipeApiMapper.ToResponse(readiness));
    }

    private async Task<ActionResult<RecipeChangeoverResponse>> TerminateAsync(
        Guid changeoverId,
        TerminateRecipeChangeoverRequest request,
        string? commandId,
        bool fail,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeChangeoverAsync(
                changeoverId,
                cancellationToken)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return authorization;
        }

        var command = new TerminateRecipeChangeoverCommand(
            changeoverId,
            request.ExpectedRevision,
            request.Reason,
            request.OccurredAtUtc,
            User.GetRequiredActorId(),
            commandId ?? string.Empty);
        var result = fail
            ? await recipeService.FailChangeoverAsync(command, cancellationToken)
                .ConfigureAwait(false)
            : await recipeService.CancelChangeoverAsync(command, cancellationToken)
                .ConfigureAwait(false);
        return result.IsFailure
            ? RecipeApiMapper.ToProblem(result.Error)
            : Ok(RecipeApiMapper.ToResponse(result.Value));
    }

    private async ValueTask<ActionResult?> AuthorizeChangeoverAsync(
        Guid changeoverId,
        CancellationToken cancellationToken)
    {
        var changeover = await recipeService
            .GetChangeoverAsync(changeoverId, cancellationToken)
            .ConfigureAwait(false);
        if (changeover.IsFailure)
        {
            return RecipeApiMapper.ToProblem(changeover.Error);
        }

        return IsAuthorizedForStation(changeover.Value.StationId)
            ? null
            : Forbid();
    }

    private bool IsAuthorizedForStation(string stationId) =>
        User.IsInRole(OpenLineOpsApiSecurity.EngineeringRole)
        || User.HasClaim(OpenLineOpsApiSecurity.StationIdClaim, stationId);
}
