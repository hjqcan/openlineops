using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Models;
using OpenLineOps.Projects.Application.Releases;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Projects.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Projects)]
[Route(OpenLineOpsApiRoutes.ProjectSnapshotProductionRunContext)]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class ProjectSnapshotProductionRunContextsController(
    IProjectReleaseProductionRunContextService contextService,
    IStationDeploymentResolver deploymentResolver) : ControllerBase
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
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        try
        {
            var context = result.Value;
            var deployment = await deploymentResolver.ResolveAsync(
                    new StationDeploymentRequest(
                        context.ProjectId,
                        context.ApplicationId,
                        context.SnapshotId,
                        context.EntryStationSystemId),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    deployment.ProductionLineDefinitionId,
                    context.ProductionLineDefinitionId,
                    StringComparison.Ordinal))
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Projects.ProjectReleaseStationDeploymentMismatch",
                    Detail = "Entry Station deployment does not match the immutable Production Line.",
                    Status = StatusCodes.Status409Conflict
                });
            }

            return Ok(ToResponse(context, deployment));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Projects.ProjectReleaseStationDeploymentInvalid",
                Detail = exception.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    private static ProjectReleaseProductionRunContextResponse ToResponse(
        ProjectReleaseProductionRunContext context,
        StationDeploymentRoute deployment) =>
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
            deployment.StationId,
            deployment.PackageContentSha256,
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
