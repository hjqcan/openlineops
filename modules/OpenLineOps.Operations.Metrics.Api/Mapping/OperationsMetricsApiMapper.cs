using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Operations.Metrics.Api.Models;
using OpenLineOps.Operations.Metrics.Domain.Downtime;
using OpenLineOps.Operations.Metrics.Domain.Metrics;
using OpenLineOps.Operations.Metrics.Domain.Production;
using OpenLineOps.Operations.Metrics.Domain.Shifts;

namespace OpenLineOps.Operations.Metrics.Api.Mapping;

internal static class OperationsMetricsApiMapper
{
    public static ProductionEventResponse ToResponse(
        CanonicalProductionEvent item) =>
        new(
            item.EventId,
            item.StationId,
            item.UnitId,
            item.Kind.ToString(),
            item.SourceTimestampUtc,
            item.OccurredAtUtc,
            item.ReceivedAtUtc,
            item.FirstAttempt,
            item.Good,
            item.CycleDuration?.TotalMilliseconds,
            item.SchemaVersion);

    public static ShiftDefinitionResponse ToResponse(ShiftDefinition item) =>
        new(
            item.ShiftId,
            item.StationId,
            item.Name,
            item.TimeZoneId,
            item.LocalStartTime,
            item.LocalEndTime,
            item.CreatedBy,
            item.CreatedAtUtc,
            item.SchemaVersion);

    public static PlannedProductionWindowResponse ToResponse(
        PlannedProductionWindow item) =>
        new(
            item.WindowId,
            item.ShiftId,
            item.StationId,
            item.StartsAtUtc,
            item.EndsAtUtc,
            item.TargetQuantity,
            item.IdealCycleTime.TotalMilliseconds,
            item.CreatedBy,
            item.CreatedAtUtc,
            item.SchemaVersion);

    public static DowntimeResponse ToResponse(DowntimeInterval item) =>
        new(
            item.DowntimeId,
            item.StationId,
            item.SourceId,
            item.StartedAtUtc,
            item.SourceClearedAtUtc,
            item.ReasonCode,
            item.ReasonComment,
            item.ReasonAttributedBy,
            item.ReasonAttributedAtUtc,
            item.Revision);

    public static OeeResponse ToResponse(OeeReport item) =>
        new(
            item.StationId,
            item.FromUtc,
            item.ToUtc,
            item.PlannedWindowCount,
            item.PlannedProductionSeconds,
            item.DowntimeSeconds,
            item.OperatingSeconds,
            item.PlannedTargetQuantity,
            item.CompletedCount,
            item.GoodCount,
            item.FirstAttemptCount,
            item.FirstPassGoodCount,
            item.CycleTimeSeconds,
            item.TaktSeconds,
            item.FirstPassYield,
            item.Yield,
            item.Availability,
            item.Performance,
            item.Quality,
            item.Oee,
            new OeeMetricDefinitionsResponse(
                OeeMetricDefinitions.CycleTime,
                OeeMetricDefinitions.Takt,
                OeeMetricDefinitions.FirstPassYield,
                OeeMetricDefinitions.Yield,
                OeeMetricDefinitions.Availability,
                OeeMetricDefinitions.Performance,
                OeeMetricDefinitions.Quality,
                OeeMetricDefinitions.Oee));

    public static ProblemDetails Problem(
        int status,
        string code,
        string detail) =>
        new()
        {
            Status = status,
            Title = code,
            Detail = detail
        };
}
