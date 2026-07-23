using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Api.Models;
using OpenLineOps.Traceability.Application.MaterialLifecycle;
using OpenLineOps.Traceability.Application.Records;

namespace OpenLineOps.Traceability.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Traceability)]
[Route(OpenLineOpsApiRoutes.Traceability + "/production-units/{productionUnitId:guid}/material-lifecycle")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = OpenLineOpsApiSecurity.OperatorPolicy)]
public sealed class ProductionUnitMaterialLifecycleController(
    IProductionUnitMaterialLifecycleReader lifecycleReader) : ControllerBase
{
    private readonly IProductionUnitMaterialLifecycleReader _lifecycleReader = lifecycleReader
        ?? throw new ArgumentNullException(nameof(lifecycleReader));

    [HttpGet]
    [ProducesResponseType<ProductionUnitMaterialLifecycleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionUnitMaterialLifecycleResponse>> GetAsync(
        Guid productionUnitId,
        CancellationToken cancellationToken)
    {
        var result = await _lifecycleReader
            .GetAsync(productionUnitId, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure ? ToProblem(result.Error) : Ok(ToResponse(result.Value));
    }

    private static ProductionUnitMaterialLifecycleResponse ToResponse(
        ProductionUnitMaterialLifecycleDetails details) => new(
        details.ProductionUnitId,
        details.ProductModelId,
        details.ProductionUnitIdentityInputKey,
        details.ProductionUnitIdentityValue,
        details.LotId,
        details.CurrentDisposition,
        details.DispositionReason,
        details.CurrentLocation is null ? null : ToResponse(details.CurrentLocation),
        details.CurrentCarrierLocation is null
            ? null
            : ToResponse(details.CurrentCarrierLocation),
        details.RegisteredAtUtc,
        details.ObservedThroughUtc,
        details.Genealogy.Select(ToResponse).ToArray(),
        details.MaterialLocationTransitions.Select(ToResponse).ToArray(),
        details.SlotOccupancyTransitions.Select(ToResponse).ToArray(),
        details.DispositionTransitions.Select(ToResponse).ToArray());

    private static TraceMaterialGenealogyResponse ToResponse(
        TraceMaterialGenealogyDetails link) => new(
        link.LinkId,
        link.ParentProductionUnitId,
        link.ChildProductionUnitId,
        link.Relationship,
        link.OperationId,
        link.LinkedBy,
        link.LinkedAtUtc);

    private static TraceMaterialLocationTransitionResponse ToResponse(
        TraceMaterialLocationTransitionDetails transition) => new(
        transition.EvidenceId,
        transition.ProductionRunId,
        transition.MaterialKind,
        transition.MaterialId,
        transition.Source is null ? null : ToResponse(transition.Source),
        ToResponse(transition.Destination),
        transition.ActorId,
        transition.OccurredAtUtc);

    private static TraceMaterialLocationResponse ToResponse(
        TraceMaterialLocationDetails location) => new(
        location.Kind,
        location.LineId,
        location.StationSystemId,
        location.SlotId,
        location.CarrierId,
        location.CarrierPositionId);

    private static TraceSlotOccupancyTransitionResponse ToResponse(
        TraceSlotOccupancyTransitionDetails transition) => new(
        transition.EvidenceId,
        transition.ProductionRunId,
        transition.LineId,
        transition.StationSystemId,
        transition.SlotId,
        transition.MaterialKind,
        transition.MaterialId,
        transition.PreviousStatus,
        transition.CurrentStatus,
        transition.ActorId,
        transition.OccurredAtUtc);

    private static TraceDispositionTransitionResponse ToResponse(
        TraceDispositionTransitionDetails transition) => new(
        transition.EvidenceId,
        transition.ProductionUnitId,
        transition.ProductionRunId,
        transition.PreviousDisposition,
        transition.CurrentDisposition,
        transition.Reason,
        transition.ActorId,
        transition.OccurredAtUtc);

    private ObjectResult ToProblem(ApplicationError error)
    {
        var statusCode = error.Code.Split('.', 2)[0] switch
        {
            "Validation" => StatusCodes.Status400BadRequest,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status409Conflict
        };
        return Problem(title: error.Code, detail: error.Message, statusCode: statusCode);
    }
}
