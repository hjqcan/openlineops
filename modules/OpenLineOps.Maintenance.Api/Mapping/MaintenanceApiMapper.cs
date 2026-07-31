using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Maintenance.Api.Models;
using OpenLineOps.Maintenance.Application.Contracts;

namespace OpenLineOps.Maintenance.Api.Mapping;

internal static class MaintenanceApiMapper
{
    public static EquipmentAssetResponse ToResponse(
        EquipmentAssetDetails details) =>
        new(
            details.AssetId,
            details.Revision,
            details.StationId,
            details.DisplayName,
            details.ProductionCritical,
            details.RequiresCalibration,
            details.TotalCycles,
            details.TotalOperatingHours,
            details.HealthStatus.ToString(),
            details.HealthDiagnostic,
            details.Calibration is null
                ? null
                : new CalibrationResponse(
                    details.Calibration.Status.ToString(),
                    details.Calibration.Reference,
                    details.Calibration.RecordedAtUtc,
                    details.Calibration.ValidUntilUtc),
            details.Plans.Select(
                    static plan => new MaintenancePlanResponse(
                        plan.PlanId,
                        plan.DisplayName,
                        plan.CycleInterval,
                        plan.OperatingHoursInterval,
                        plan.CalendarInterval is null
                            ? null
                            : checked((int)plan.CalendarInterval.Value.TotalSeconds),
                        plan.BlocksProduction,
                        plan.BaselineCycles,
                        plan.BaselineOperatingHours,
                        plan.NextDueAtUtc))
                .ToArray(),
            details.Tasks.Select(
                    static task => new MaintenanceTaskResponse(
                        task.TaskId,
                        task.PlanId,
                        task.DueReason.ToString(),
                        task.DueAtUtc,
                        task.DueAtCycles,
                        task.DueAtOperatingHours,
                        task.Status.ToString(),
                        task.CompletedAtUtc,
                        task.CompletedBy,
                        task.CompletionNote))
                .ToArray());

    public static StationProductionReadinessResponse ToResponse(
        StationProductionStartDecision decision) =>
        new(
            decision.StationId,
            decision.EvaluatedAtUtc,
            decision.Allowed,
            decision.Blocks.Select(
                    static block => new ProductionStartBlockResponse(
                        block.AssetId,
                        block.Reason.ToString(),
                        block.SubjectId,
                        block.Detail))
                .ToArray(),
            decision.AssetRevisions.Select(
                    static revision => new EquipmentAssetRevisionResponse(
                        revision.AssetId,
                        revision.Revision))
                .ToArray());

    public static ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };
        return new ObjectResult(
            new ProblemDetails
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
