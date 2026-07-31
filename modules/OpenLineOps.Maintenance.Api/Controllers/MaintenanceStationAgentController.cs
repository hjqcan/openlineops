using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Maintenance.Api.Mapping;
using OpenLineOps.Maintenance.Api.Models;
using OpenLineOps.Maintenance.Application.Contracts;
using OpenLineOps.Maintenance.Application.Services;
using OpenLineOps.Maintenance.Domain.Assets;

namespace OpenLineOps.Maintenance.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Operations)]
[Route(MaintenanceApiRoutes.Root)]
[Authorize(Policy = OpenLineOpsApiSecurity.StationAgentPolicy)]
public sealed class MaintenanceStationAgentController(
    IEquipmentMaintenanceService maintenanceService) : ControllerBase
{
    [HttpPost("assets/{assetId}/usage")]
    [ProducesResponseType<EquipmentAssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EquipmentAssetResponse>> RecordUsageAsync(
        string assetId,
        RecordEquipmentUsageRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAssetAsync(
                assetId,
                cancellationToken)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return authorization;
        }

        var result = await maintenanceService.RecordUsageAsync(
                new RecordEquipmentUsageCommand(
                    assetId,
                    request.CycleDelta,
                    request.OperatingHoursDelta,
                    idempotencyKey ?? string.Empty,
                    User.GetRequiredActorId(),
                    request.OccurredAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? MaintenanceApiMapper.ToProblem(result.Error)
            : Ok(MaintenanceApiMapper.ToResponse(result.Value));
    }

    [HttpPost("assets/{assetId}/due-evaluations")]
    [ProducesResponseType<EquipmentAssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EquipmentAssetResponse>>
        EvaluateMaintenanceDueAsync(
            string assetId,
            EvaluateMaintenanceDueRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAssetAsync(
                assetId,
                cancellationToken)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return authorization;
        }

        var result = await maintenanceService.EvaluateMaintenanceAsync(
                new EvaluateMaintenanceCommand(
                    assetId,
                    idempotencyKey ?? string.Empty,
                    User.GetRequiredActorId(),
                    request.EvaluatedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? MaintenanceApiMapper.ToProblem(result.Error)
            : Ok(MaintenanceApiMapper.ToResponse(result.Value));
    }

    [HttpPost("assets/{assetId}/health")]
    [ProducesResponseType<EquipmentAssetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EquipmentAssetResponse>> RecordHealthAsync(
        string assetId,
        RecordEquipmentHealthRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAssetAsync(
                assetId,
                cancellationToken)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return authorization;
        }

        if (!TryParseDefined(request.Status, out EquipmentHealthStatus status))
        {
            return MaintenanceApiMapper.ToProblem(
                ApplicationError.Validation(
                    "Maintenance.Health.Status",
                    "Equipment health status must be an exact defined status name."));
        }

        var result = await maintenanceService.RecordHealthAsync(
                new RecordEquipmentHealthCommand(
                    assetId,
                    status,
                    request.Diagnostic,
                    idempotencyKey ?? string.Empty,
                    User.GetRequiredActorId(),
                    request.OccurredAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure
            ? MaintenanceApiMapper.ToProblem(result.Error)
            : Ok(MaintenanceApiMapper.ToResponse(result.Value));
    }

    private async ValueTask<ActionResult?> AuthorizeAssetAsync(
        string assetId,
        CancellationToken cancellationToken)
    {
        var asset = await maintenanceService.GetAsync(assetId, cancellationToken)
            .ConfigureAwait(false);
        if (asset.IsFailure)
        {
            return MaintenanceApiMapper.ToProblem(asset.Error);
        }

        return string.Equals(
                asset.Value.StationId,
                User.GetRequiredStationId(),
                StringComparison.Ordinal)
            ? null
            : Forbid();
    }

    private static bool TryParseDefined<TEnum>(
        string value,
        out TEnum parsed)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed)
        && Enum.IsDefined(parsed);
}
