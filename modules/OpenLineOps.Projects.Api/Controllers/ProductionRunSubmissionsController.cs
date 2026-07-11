using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Projects.Api.Integrations;
using OpenLineOps.Projects.Api.Models;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Runtime.Application.Runs;

namespace OpenLineOps.Projects.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Projects)]
[Route(OpenLineOpsApiRoutes.ProductionRuns)]
public sealed class ProductionRunSubmissionsController(
    IAutomationProjectService projectService,
    IProjectReleaseProductionRunLauncher productionRunLauncher) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ProductionRunReadModel>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionRunReadModel>> SubmitAsync(
        SubmitPublishedProductionRunRequest? request,
        CancellationToken cancellationToken)
    {
        var validationErrors = Validate(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var projectId = request!.ProjectId!;
        var snapshotId = request.ProjectSnapshotId!;
        var projectResult = await projectService
            .GetByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (projectResult.IsFailure)
        {
            return ToProblem(projectResult.Error);
        }

        var snapshot = projectResult.Value.Snapshots.SingleOrDefault(candidate =>
            string.Equals(candidate.SnapshotId, snapshotId, StringComparison.Ordinal));
        if (snapshot is null)
        {
            return ToProblem(ApplicationError.NotFound(
                "Projects.ProjectSnapshotNotFound",
                $"Project snapshot {snapshotId} was not found in automation project {projectId}."));
        }

        var submitResult = await productionRunLauncher
            .SubmitAsync(
                snapshot,
                new SubmitProjectReleaseProductionRunRequest(
                    Guid.ParseExact(request.ProductionRunId!, "D"),
                    Guid.ParseExact(request.ProductionUnitId!, "D"),
                    request.ActorId!),
                cancellationToken)
            .ConfigureAwait(false);
        if (submitResult.IsFailure)
        {
            return ToProblem(submitResult.Error);
        }

        var response = ProductionRunReadModelMapper.ToReadModel(submitResult.Value);
        return Accepted($"/api/production-runs/{response.ProductionRunId:D}", response);
    }

    private static Dictionary<string, string[]> Validate(SubmitPublishedProductionRunRequest? request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request is null)
        {
            errors[nameof(request)] = ["Request body is required."];
            return errors;
        }

        AddCanonicalText(errors, nameof(request.ProjectId), request.ProjectId);
        AddCanonicalText(errors, nameof(request.ProjectSnapshotId), request.ProjectSnapshotId);
        AddCanonicalGuid(errors, nameof(request.ProductionRunId), request.ProductionRunId);
        AddCanonicalGuid(errors, nameof(request.ProductionUnitId), request.ProductionUnitId);
        AddCanonicalText(errors, nameof(request.ActorId), request.ActorId);
        return errors;
    }

    private static void AddCanonicalText(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            errors[key] = ["Value must be non-empty canonical text."];
        }
    }

    private static void AddCanonicalGuid(
        Dictionary<string, string[]> errors,
        string key,
        string? value)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed)
            || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            errors[key] = ["Value must be a non-empty canonical lowercase D-format UUID."];
        }
    }

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
