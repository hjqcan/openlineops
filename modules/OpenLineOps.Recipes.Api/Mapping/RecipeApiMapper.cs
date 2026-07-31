using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Recipes.Api.Models;
using OpenLineOps.Recipes.Application.Readiness;
using OpenLineOps.Recipes.Domain.Assignments;
using OpenLineOps.Recipes.Domain.Changeovers;
using OpenLineOps.Recipes.Domain.Deployments;
using OpenLineOps.Recipes.Domain.Recipes;

namespace OpenLineOps.Recipes.Api.Mapping;

internal static class RecipeApiMapper
{
    public static RecipeRevisionSnapshotResponse ToResponse(
        RecipeRevisionSnapshot snapshot) =>
        new(
            snapshot.RecipeId,
            snapshot.VersionId,
            snapshot.DisplayName,
            snapshot.ReleasedAtUtc,
            snapshot.ConfigurationSha256,
            snapshot.Parameters.Select(static parameter =>
                    new RecipeParameterSnapshotResponse(
                        parameter.Key,
                        parameter.Type.ToString(),
                        parameter.Unit,
                        parameter.CanonicalValue,
                        parameter.Required))
                .ToArray());

    public static RecipeAssignmentResponse ToResponse(RecipeAssignment assignment) =>
        new(
            assignment.AssignmentId,
            assignment.Revision,
            assignment.RecipeId,
            assignment.VersionId,
            assignment.ProductModelId,
            assignment.StationId,
            assignment.EffectiveFromUtc,
            assignment.EffectiveUntilUtc,
            assignment.ConfigurationSha256,
            assignment.CreatedAtUtc,
            assignment.CreatedBy);

    public static RecipeDeploymentResponse ToResponse(RecipeDeployment deployment) =>
        new(
            deployment.DeploymentId,
            deployment.AssignmentId,
            deployment.Revision,
            deployment.RecipeId,
            deployment.VersionId,
            deployment.ProductModelId,
            deployment.StationId,
            deployment.Status.ToString(),
            ToResponse(deployment.Snapshot),
            new DeploymentCommandResponse(
                deployment.Command.CommandId,
                deployment.Command.FencingToken,
                deployment.Command.DeadlineUtc),
            deployment.CreatedAtUtc,
            deployment.CreatedBy,
            deployment.Verification is null
                ? null
                : new RecipeVerificationResponse(
                    deployment.Verification.VerificationId,
                    deployment.Verification.VerifiedAtUtc,
                    deployment.Verification.VerifiedBy,
                    deployment.Verification.Succeeded,
                    deployment.Verification.ReadbackSha256,
                    deployment.Verification.Parameters.Select(static parameter =>
                            new RecipeParameterVerificationResponse(
                                parameter.Key,
                                parameter.TypeMatches,
                                parameter.UnitMatches,
                                parameter.ValueMatches,
                                parameter.ExpectedType,
                                parameter.ActualType,
                                parameter.ExpectedUnit,
                                parameter.ActualUnit,
                                parameter.ExpectedValue,
                                parameter.ActualValue))
                        .ToArray()));

    public static RecipeChangeoverResponse ToResponse(RecipeChangeover changeover) =>
        new(
            changeover.ChangeoverId,
            changeover.AssignmentId,
            changeover.Revision,
            changeover.RecipeId,
            changeover.VersionId,
            changeover.ProductModelId,
            changeover.StationId,
            changeover.State.ToString(),
            changeover.DeploymentId,
            changeover.StartedAtUtc,
            changeover.StartedBy,
            changeover.Audit.Select(static entry =>
                    new RecipeChangeoverAuditResponse(
                        entry.Revision,
                        entry.Action,
                        entry.State.ToString(),
                        entry.Evidence,
                        entry.WorkInProgressCount,
                        entry.LineClearanceConfirmed,
                        entry.OccurredAtUtc,
                        entry.ActorId))
                .ToArray());

    public static RecipeProductionReadinessResponse ToResponse(
        RecipeProductionReadinessResult readiness) =>
        new(
            readiness.Allowed,
            readiness.AssignmentId,
            readiness.DeploymentId,
            readiness.ConfigurationSha256,
            readiness.Blocks.Select(static block =>
                    new RecipeProductionReadinessBlockResponse(
                        block.Code,
                        block.Detail))
                .ToArray());

    public static ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };
        return new ObjectResult(new ProblemDetails
        {
            Status = statusCode,
            Title = error.Code,
            Detail = error.Message
        })
        {
            StatusCode = statusCode
        };
    }
}
