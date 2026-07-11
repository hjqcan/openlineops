using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.ProductionUnits;

namespace OpenLineOps.Runtime.Application.Materials;

public enum ProductionMaterialArrivalClaimStatus
{
    Claimed = 1,
    Busy = 2,
    Completed = 3
}

public sealed record ProductionMaterialArrivalClaim(
    ProductionMaterialArrivalClaimStatus Status,
    Guid? ClaimToken,
    DateTimeOffset? RetryAtUtc,
    RuntimeOperationResult? Result);

public interface IProductionMaterialArrivalInbox
{
    ValueTask<ProductionMaterialArrivalClaim> ClaimAsync(
        MaterialArrived message,
        DateTimeOffset claimedAtUtc,
        TimeSpan claimDuration,
        CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(
        Guid messageId,
        Guid claimToken,
        RuntimeOperationResult result,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class ProductionMaterialArrivalIngress(
    IProductionMaterialArrivalInbox inbox,
    IProductionMaterialRepository materialRepository,
    ProductionMaterialService materialService,
    IClock clock)
{
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BusyPollDelay = TimeSpan.FromMilliseconds(50);

    public async ValueTask<RuntimeOperationResult> HandleAsync(
        MaterialArrived message,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        while (true)
        {
            var nowUtc = RequireUtc(clock.UtcNow);
            var claim = await inbox.ClaimAsync(
                    message,
                    nowUtc,
                    ClaimDuration,
                    cancellationToken)
                .ConfigureAwait(false);
            switch (claim.Status)
            {
                case ProductionMaterialArrivalClaimStatus.Completed:
                    return claim.Result ?? throw new InvalidDataException(
                        "Completed material arrival Inbox item has no result evidence.");
                case ProductionMaterialArrivalClaimStatus.Busy:
                {
                    if (claim.RetryAtUtc is null || claim.RetryAtUtc <= nowUtc)
                    {
                        throw new InvalidDataException(
                            "Busy material arrival Inbox item has no future retry timestamp.");
                    }

                    var delay = claim.RetryAtUtc.Value - nowUtc;
                    await Task.Delay(
                            delay < BusyPollDelay ? delay : BusyPollDelay,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                case ProductionMaterialArrivalClaimStatus.Claimed:
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported material arrival claim state {claim.Status}.");
            }

            var claimToken = claim.ClaimToken is { } token && token != Guid.Empty
                ? token
                : throw new InvalidDataException(
                    "Claimed material arrival Inbox item has no claim token.");
            var result = await ApplyOrReconcileAsync(message, cancellationToken)
                .ConfigureAwait(false);
            await inbox.CompleteAsync(
                    message.MessageId,
                    claimToken,
                    result,
                    RequireUtc(clock.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
    }

    private async ValueTask<RuntimeOperationResult> ApplyOrReconcileAsync(
        MaterialArrived message,
        CancellationToken cancellationToken)
    {
        var productionUnitId = new ProductionUnitId(message.ProductionUnitId);
        var destination = MaterialLocation.AtStation(message.LineId, message.StationSystemId);
        var current = await materialRepository.GetProductionUnitAsync(
                productionUnitId,
                cancellationToken)
            .ConfigureAwait(false);
        if (current is not null
            && Equals(current.Aggregate.Location, destination)
            && current.Aggregate.LastLocationTransitionAtUtc == message.ArrivedAtUtc)
        {
            return RuntimeOperationResult.Accepted(
                "Production Unit arrival was reconciled from persisted material evidence.");
        }

        return await materialService.ArriveAsync(
                new ArriveMaterialCommand(
                    MaterialReference.ForProductionUnit(productionUnitId),
                    destination,
                    message.ActorId,
                    message.ArrivedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Material arrival Inbox clock must return a non-default UTC value.");
        }

        return value;
    }
}
