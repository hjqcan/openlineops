using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Models;
using OpenLineOps.Projects.Application.Releases;

namespace OpenLineOps.Projects.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Projects)]
[Route(OpenLineOpsApiRoutes.ProjectSnapshotProductionRunContext)]
public sealed class ProjectSnapshotProductionRunContextsController(
    IProjectReleaseProductionRunContextService contextService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ProjectReleaseProductionRunContextResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProjectReleaseProductionRunContextResponse>> GetAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        var result = await contextService
            .GetAsync(projectId, snapshotId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? ToProblem(result.Error)
            : Ok(ToResponse(result.Value));
    }

    private static ProjectReleaseProductionRunContextResponse ToResponse(
        ProjectReleaseProductionRunContext context) =>
        new(
            context.ProjectId,
            context.ApplicationId,
            context.SnapshotId,
            context.TopologyId,
            context.ProductionLineDefinitionId,
            context.ProductModelId,
            context.ProductModelIdentityInputKey,
            context.EntryOperationId,
            context.EntryStationSystemId,
            context.StationSystemIds);

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode);
    }
}
