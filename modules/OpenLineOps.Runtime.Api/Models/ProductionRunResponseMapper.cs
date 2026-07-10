using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Api.Models;

internal static class ProductionRunResponseMapper
{
    public static ProductionRunResponse ToResponse(ProductionRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var stages = run.Stages.Select(stage => new ProductionRunStageResponse(
            stage.StageId,
            stage.Sequence,
            stage.WorkstationId,
            stage.StationId.Value,
            stage.ProcessDefinitionId.Value,
            stage.ProcessVersionId.Value,
            stage.ConfigurationSnapshotId.Value,
            stage.RecipeSnapshotId.Value,
            stage.Status.ToString(),
            IsTerminal(stage.Status),
            stage.RuntimeSessionId?.Value,
            stage.StartedAtUtc,
            stage.CompletedAtUtc,
            stage.FailureCode,
            stage.FailureReason,
            stage.CompletedStepCount,
            stage.CommandCount,
            stage.IncidentCount)).ToArray();

        return new ProductionRunResponse(
            run.RunId.Value,
            run.ProjectId,
            run.ApplicationId,
            run.ProjectSnapshotId,
            run.TopologyId,
            run.ProductionLineDefinitionId,
            new RuntimeDutIdentityResponse(
                run.DutIdentity.ModelId,
                run.DutIdentity.InputKey,
                run.DutIdentity.Value),
            run.BatchId,
            run.FixtureId,
            run.DeviceId,
            run.ActorId,
            run.Status.ToString(),
            run.Status is ProductionRunStatus.Completed
                or ProductionRunStatus.Failed
                or ProductionRunStatus.Canceled,
            run.CreatedAtUtc,
            run.LastTransitionAtUtc,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.FailureCode,
            run.FailureReason,
            run.Stages.Count(stage => stage.Status == ProductionStageRunStatus.Completed),
            stages.Sum(stage => stage.CompletedStepCount),
            stages.Sum(stage => stage.CommandCount),
            stages.Sum(stage => stage.IncidentCount),
            stages);
    }

    private static bool IsTerminal(ProductionStageRunStatus status)
    {
        return status is ProductionStageRunStatus.Completed
            or ProductionStageRunStatus.Failed
            or ProductionStageRunStatus.Canceled
            or ProductionStageRunStatus.Skipped;
    }
}
