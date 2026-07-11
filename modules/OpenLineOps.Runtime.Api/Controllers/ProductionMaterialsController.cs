using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Runtime.Api.Models;
using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Occupancy;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Api.Controllers;

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route(OpenLineOpsApiRoutes.ProductionUnits)]
public sealed class ProductionUnitsController(
    ProductionMaterialService service,
    IProductionMaterialRepository repository) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ProductionUnitApiResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionUnitApiResponse>> RegisterAsync(
        RegisterProductionUnitApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = new ProductionUnitId(request.ProductionUnitId);
            var result = await service.RegisterUnitAsync(
                    new RegisterProductionUnitCommand(
                        id,
                        request.ProductModelId,
                        request.IdentityKey,
                        request.IdentityValue,
                        request.LotId is null ? null : new ProductionLotId(request.LotId),
                        request.ActorId,
                        request.OccurredAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return ProductionMaterialApi.ToProblem(this, result);
            }

            var entry = await repository.GetProductionUnitAsync(id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Registered Production Unit was not persisted.");
            return CreatedAtAction(
                nameof(GetAsync),
                new { productionUnitId = id.ToString() },
                ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpGet("{productionUnitId}")]
    [ProducesResponseType<ProductionUnitApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductionUnitApiResponse>> GetAsync(
        string productionUnitId,
        CancellationToken cancellationToken)
    {
        if (!ProductionMaterialApi.TryParseProductionUnitId(productionUnitId, out var id))
        {
            return BadRequest();
        }

        var entry = await repository.GetProductionUnitAsync(id, cancellationToken)
            .ConfigureAwait(false);
        return entry is null
            ? NotFound()
            : Ok(ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
    }

    [HttpPost("{productionUnitId}/arrivals")]
    [ProducesResponseType<ProductionUnitApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionUnitApiResponse>> ArriveAsync(
        string productionUnitId,
        MaterialArrivalApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!ProductionMaterialApi.TryParseProductionUnitId(productionUnitId, out var id))
        {
            return BadRequest();
        }

        try
        {
            var result = await service.ArriveAsync(
                    new ArriveMaterialCommand(
                        MaterialReference.ForProductionUnit(id),
                        MaterialLocation.AtStation(request.LineId, request.StationSystemId),
                        request.ActorId,
                        request.OccurredAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            return await ResultAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpPost("{productionUnitId}/commands/{command}")]
    [ProducesResponseType<ProductionUnitApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionUnitApiResponse>> CommandAsync(
        string productionUnitId,
        string command,
        ProductionUnitDispositionCommandApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!ProductionMaterialApi.TryParseProductionUnitId(productionUnitId, out var id)
            || command is not ("Hold" or "Release" or "Scrap"))
        {
            return BadRequest();
        }

        try
        {
            var result = command switch
            {
                "Hold" => await service.HoldAsync(
                        new HoldProductionUnitCommand(
                            id,
                            ProductionMaterialApi.RequiredReason(request.Reason, command),
                            request.ActorId,
                            request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                "Release" => await service.ReleaseAsync(
                        new ReleaseProductionUnitCommand(id, request.ActorId, request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                "Scrap" => await service.ScrapAsync(
                        new ScrapProductionUnitCommand(
                            id,
                            ProductionMaterialApi.RequiredReason(request.Reason, command),
                            request.ActorId,
                            request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported Production Unit command {command}.")
            };
            return await ResultAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpPost("{productionUnitId}/transfers")]
    [ProducesResponseType<ProductionUnitApiResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductionUnitApiResponse>> TransferAsync(
        string productionUnitId,
        MaterialTransferApiRequest request,
        CancellationToken cancellationToken)
    {
        if (!ProductionMaterialApi.TryParseProductionUnitId(productionUnitId, out var id))
        {
            return BadRequest();
        }

        try
        {
            var result = await service.TransferAsync(
                    new TransferMaterialCommand(
                        MaterialReference.ForProductionUnit(id),
                        ProductionMaterialApi.ToDomain(request.ExpectedLocation),
                        ProductionMaterialApi.ToDomain(request.Destination),
                        request.ActorId,
                        request.OccurredAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            return await ResultAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    private async Task<ActionResult<ProductionUnitApiResponse>> ResultAsync(
        ProductionUnitId id,
        RuntimeOperationResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Succeeded)
        {
            return ProductionMaterialApi.ToProblem(this, result);
        }

        var entry = await repository.GetProductionUnitAsync(id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Production Unit command committed no state.");
        return Ok(ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route(OpenLineOpsApiRoutes.ProductionLots)]
public sealed class ProductionLotsController(
    ProductionMaterialService service,
    IProductionMaterialRepository repository) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ProductionLotApiResponse>> RegisterAsync(
        RegisterProductionLotApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = new ProductionLotId(request.LotId);
            var result = await service.RegisterLotAsync(
                    new RegisterProductionLotCommand(
                        id,
                        request.ProductModelId,
                        request.DeclaredQuantity,
                        request.ActorId,
                        request.OccurredAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return ProductionMaterialApi.ToProblem(this, result);
            }

            var entry = await repository.GetProductionLotAsync(id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Registered Production Lot was not persisted.");
            return CreatedAtAction(
                nameof(GetAsync),
                new { lotId = id.Value },
                ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpGet("{lotId}")]
    public async Task<ActionResult<ProductionLotApiResponse>> GetAsync(
        string lotId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await repository.GetProductionLotAsync(
                    new ProductionLotId(lotId),
                    cancellationToken)
                .ConfigureAwait(false);
            return entry is null
                ? NotFound()
                : Ok(ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
        }
        catch (ArgumentException exception)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route(OpenLineOpsApiRoutes.ProductionCarriers)]
public sealed class ProductionCarriersController(
    ProductionMaterialService service,
    IProductionMaterialRepository repository) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ProductionCarrierApiResponse>> RegisterAsync(
        RegisterProductionCarrierApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = new CarrierId(request.CarrierId);
            var result = await service.RegisterCarrierAsync(
                    new RegisterCarrierCommand(
                        id,
                        request.CarrierTypeId,
                        request.Capacity,
                        request.ActorId,
                        request.OccurredAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return ProductionMaterialApi.ToProblem(this, result);
            }

            return await CreatedAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpGet("{carrierId}")]
    public async Task<ActionResult<ProductionCarrierApiResponse>> GetAsync(
        string carrierId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await repository.GetCarrierAsync(new CarrierId(carrierId), cancellationToken)
                .ConfigureAwait(false);
            return entry is null
                ? NotFound()
                : Ok(ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
        }
        catch (ArgumentException exception)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpPost("{carrierId}/arrivals")]
    public async Task<ActionResult<ProductionCarrierApiResponse>> ArriveAsync(
        string carrierId,
        MaterialArrivalApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = new CarrierId(carrierId);
            var result = await service.ArriveAsync(
                    new ArriveMaterialCommand(
                        MaterialReference.ForCarrier(id),
                        MaterialLocation.AtStation(request.LineId, request.StationSystemId),
                        request.ActorId,
                        request.OccurredAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            return await ResultAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpPost("{carrierId}/transfers")]
    public async Task<ActionResult<ProductionCarrierApiResponse>> TransferAsync(
        string carrierId,
        MaterialTransferApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = new CarrierId(carrierId);
            var result = await service.TransferAsync(
                    new TransferMaterialCommand(
                        MaterialReference.ForCarrier(id),
                        ProductionMaterialApi.ToDomain(request.ExpectedLocation),
                        ProductionMaterialApi.ToDomain(request.Destination),
                        request.ActorId,
                        request.OccurredAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            return await ResultAsync(id, result, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    private async Task<ActionResult<ProductionCarrierApiResponse>> CreatedAsync(
        CarrierId id,
        CancellationToken cancellationToken)
    {
        var entry = await repository.GetCarrierAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Registered Carrier was not persisted.");
        return CreatedAtAction(
            nameof(GetAsync),
            new { carrierId = id.Value },
            ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
    }

    private async Task<ActionResult<ProductionCarrierApiResponse>> ResultAsync(
        CarrierId id,
        RuntimeOperationResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Succeeded)
        {
            return ProductionMaterialApi.ToProblem(this, result);
        }

        var entry = await repository.GetCarrierAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Carrier command committed no state.");
        return Ok(ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route(OpenLineOpsApiRoutes.SlotOccupancies)]
public sealed class SlotOccupanciesController(
    ProductionMaterialService service,
    IProductionMaterialRepository repository) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SlotOccupancyApiResponse>> RegisterAsync(
        RegisterSlotOccupancyApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var address = new SlotAddress(request.LineId, request.StationSystemId, request.SlotId);
            var result = await service.RegisterSlotAsync(
                    new RegisterSlotCommand(address, request.ActorId, request.OccurredAtUtc),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return ProductionMaterialApi.ToProblem(this, result);
            }

            var entry = await repository.GetSlotAsync(address, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Registered Slot occupancy was not persisted.");
            return CreatedAtAction(
                nameof(GetAsync),
                new
                {
                    lineId = address.LineId,
                    stationSystemId = address.StationSystemId,
                    slotId = address.SlotId
                },
                ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpGet("{lineId}/{stationSystemId}/{slotId}")]
    public async Task<ActionResult<SlotOccupancyApiResponse>> GetAsync(
        string lineId,
        string stationSystemId,
        string slotId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await repository.GetSlotAsync(
                    new SlotAddress(lineId, stationSystemId, slotId),
                    cancellationToken)
                .ConfigureAwait(false);
            return entry is null
                ? NotFound()
                : Ok(ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
        }
        catch (ArgumentException exception)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }

    [HttpPost("{lineId}/{stationSystemId}/{slotId}/commands/{command}")]
    public async Task<ActionResult<SlotOccupancyApiResponse>> CommandAsync(
        string lineId,
        string stationSystemId,
        string slotId,
        string command,
        SlotOccupancyCommandApiRequest request,
        CancellationToken cancellationToken)
    {
        if (command is not ("Reserve" or "ReleaseReservation" or "Load" or "Start" or "Complete" or "Unload"))
        {
            return BadRequest();
        }

        try
        {
            var address = new SlotAddress(lineId, stationSystemId, slotId);
            var material = ProductionMaterialApi.ToMaterial(request.MaterialKind, request.MaterialId);
            var result = command switch
            {
                "Reserve" => await service.ReserveSlotAsync(
                        new ReserveSlotCommand(address, material, request.ActorId, request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                "ReleaseReservation" => await service.ReleaseSlotReservationAsync(
                        new ReleaseSlotReservationCommand(
                            address,
                            material,
                            request.ActorId,
                            request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                "Load" => await service.LoadSlotAsync(
                        new LoadSlotCommand(address, material, request.ActorId, request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                "Start" => await service.StartSlotAsync(
                        new StartSlotCommand(address, material, request.ActorId, request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                "Complete" => await service.CompleteSlotAsync(
                        new CompleteSlotCommand(address, material, request.ActorId, request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                "Unload" => await service.UnloadSlotAsync(
                        new UnloadSlotCommand(
                            address,
                            material,
                            ProductionMaterialApi.ToDomain(request.Destination
                                ?? throw new ArgumentException(
                                    "Unload requires a destination.",
                                    nameof(request))),
                            request.ActorId,
                            request.OccurredAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported Slot command {command}.")
            };
            if (!result.Succeeded)
            {
                return ProductionMaterialApi.ToProblem(this, result);
            }

            var entry = await repository.GetSlotAsync(address, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Slot command committed no state.");
            return Ok(ProductionMaterialApi.ToResponse(entry.Aggregate.ToSnapshot()));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = OpenLineOpsApiGroups.Runtime)]
[Route(OpenLineOpsApiRoutes.MaterialGenealogy)]
public sealed class MaterialGenealogyController(ProductionMaterialService service) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<MaterialGenealogyApiResponse>> LinkAsync(
        LinkMaterialGenealogyApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = new LinkMaterialGenealogyCommand(
                new MaterialGenealogyLinkId(request.LinkId),
                new ProductionUnitId(request.ParentProductionUnitId),
                new ProductionUnitId(request.ChildProductionUnitId),
                request.Relationship,
                request.OperationId,
                request.ActorId,
                request.OccurredAtUtc);
            var result = await service.LinkGenealogyAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return ProductionMaterialApi.ToProblem(this, result);
            }

            return Created(
                $"/{OpenLineOpsApiRoutes.MaterialGenealogy}/{request.LinkId:D}",
                new MaterialGenealogyApiResponse(
                    request.LinkId,
                    request.ParentProductionUnitId,
                    request.ChildProductionUnitId,
                    request.Relationship,
                    request.OperationId,
                    request.ActorId,
                    request.OccurredAtUtc));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException)
        {
            return ProductionMaterialApi.Validation(this, exception);
        }
    }
}

internal static class ProductionMaterialApi
{
    public static ActionResult ToProblem(ControllerBase controller, RuntimeOperationResult result)
    {
        var problem = new ProblemDetails
        {
            Title = result.Code,
            Detail = result.Message
        };
        if (string.Equals(result.Code, "Runtime.ProductionMaterialNotFound", StringComparison.Ordinal))
        {
            problem.Status = StatusCodes.Status404NotFound;
            return controller.NotFound(problem);
        }

        problem.Status = StatusCodes.Status409Conflict;
        return controller.Conflict(problem);
    }

    public static ActionResult Validation(ControllerBase controller, Exception exception)
    {
        controller.ModelState.AddModelError(string.Empty, exception.Message);
        return controller.ValidationProblem(controller.ModelState);
    }

    public static bool TryParseProductionUnitId(string value, out ProductionUnitId id)
    {
        if (Guid.TryParseExact(value, "D", out var parsed)
            && parsed != Guid.Empty
            && string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
        {
            id = new ProductionUnitId(parsed);
            return true;
        }

        id = default;
        return false;
    }

    public static string RequiredReason(string? reason, string command)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException($"{command} requires a reason.", nameof(reason))
            : reason;
    }

    public static MaterialReference ToMaterial(string kind, string value) => kind switch
    {
        "ProductionUnit" => new MaterialReference(MaterialKind.ProductionUnit, value),
        "Carrier" => new MaterialReference(MaterialKind.Carrier, value),
        _ => throw new ArgumentException(
            "Material kind must be exactly ProductionUnit or Carrier.",
            nameof(kind))
    };

    public static MaterialLocation ToDomain(MaterialLocationApiRequest location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location.Kind switch
        {
            "StationQueue" when location.LineId is not null
                                && location.StationSystemId is not null
                                && location.SlotId is null
                                && location.CarrierId is null
                                && location.CarrierPositionId is null =>
                MaterialLocation.AtStation(location.LineId, location.StationSystemId),
            "Slot" when location.LineId is not null
                        && location.StationSystemId is not null
                        && location.SlotId is not null
                        && location.CarrierId is null
                        && location.CarrierPositionId is null =>
                MaterialLocation.InSlot(new SlotAddress(
                    location.LineId,
                    location.StationSystemId,
                    location.SlotId)),
            "CarrierPosition" when location.LineId is null
                                   && location.StationSystemId is null
                                   && location.SlotId is null
                                   && location.CarrierId is not null
                                   && location.CarrierPositionId is not null =>
                MaterialLocation.OnCarrier(
                    new CarrierId(location.CarrierId),
                    location.CarrierPositionId),
            _ => throw new ArgumentException(
                "Material location fields do not match its exact kind.",
                nameof(location))
        };
    }

    public static ProductionUnitApiResponse ToResponse(ProductionUnitSnapshot snapshot) => new(
        snapshot.Id.Value,
        snapshot.ProductModelId,
        snapshot.IdentityKey,
        snapshot.IdentityValue,
        snapshot.LotId?.Value,
        snapshot.RegisteredBy,
        snapshot.RegisteredAtUtc,
        snapshot.LastTransitionAtUtc,
        snapshot.Disposition.ToString(),
        snapshot.DispositionBeforeHold?.ToString(),
        snapshot.DispositionReason,
        ToResponse(snapshot.Location));

    public static ProductionLotApiResponse ToResponse(ProductionLotSnapshot snapshot) => new(
        snapshot.Id.Value,
        snapshot.ProductModelId,
        snapshot.DeclaredQuantity,
        snapshot.RegisteredBy,
        snapshot.RegisteredAtUtc);

    public static ProductionCarrierApiResponse ToResponse(CarrierSnapshot snapshot) => new(
        snapshot.Id.Value,
        snapshot.CarrierTypeId,
        snapshot.Capacity,
        snapshot.RegisteredBy,
        snapshot.RegisteredAtUtc,
        snapshot.LastTransitionAtUtc,
        ToResponse(snapshot.Location));

    public static SlotOccupancyApiResponse ToResponse(SlotOccupancySnapshot snapshot) => new(
        snapshot.Address.LineId,
        snapshot.Address.StationSystemId,
        snapshot.Address.SlotId,
        snapshot.Status.ToString(),
        snapshot.Material?.Kind.ToString(),
        snapshot.Material?.Value,
        snapshot.RegisteredAtUtc,
        snapshot.LastTransitionAtUtc);

    private static MaterialLocationApiResponse? ToResponse(MaterialLocation? location) =>
        location is null
            ? null
            : new MaterialLocationApiResponse(
                location.Kind.ToString(),
                location.LineId,
                location.StationSystemId,
                location.SlotId,
                location.CarrierId?.Value,
                location.CarrierPositionId);
}
