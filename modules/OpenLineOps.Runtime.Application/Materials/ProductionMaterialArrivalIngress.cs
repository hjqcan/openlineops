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

public enum ProductionMaterialArrivalOrigin
{
    CoordinatorApi = 1,
    StationAgent = 2
}

public interface IProductionMaterialArrivalAuthorizer
{
    ValueTask AuthorizeAsync(
        MaterialArrived message,
        ProductionMaterialArrivalOrigin origin,
        CancellationToken cancellationToken = default);
}

public sealed class RejectingProductionMaterialArrivalAuthorizer :
    IProductionMaterialArrivalAuthorizer
{
    public ValueTask AuthorizeAsync(
        MaterialArrived message,
        ProductionMaterialArrivalOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidDataException(
            $"Material arrival origin {origin} has no verified frozen deployment authorizer.");
    }
}

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
    IProductionMaterialArrivalAuthorizer authorizer,
    IProductionMaterialRepository materialRepository,
    ProductionMaterialService materialService,
    IClock clock)
{
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BusyPollDelay = TimeSpan.FromMilliseconds(50);

    public async ValueTask<RuntimeOperationResult> HandleAsync(
        MaterialArrived message,
        ProductionMaterialArrivalOrigin origin,
        CancellationToken cancellationToken = default)
    {
        StationMessageContract.Validate(message);
        if (!Enum.IsDefined(origin))
        {
            throw new InvalidDataException(
                $"Unsupported material arrival origin {origin}.");
        }

        await authorizer.AuthorizeAsync(message, origin, cancellationToken)
            .ConfigureAwait(false);
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
        var material = ToMaterialReference(message);
        var destination = MaterialLocation.AtStation(message.LineId, message.StationSystemId);
        if (await HasArrivalEvidenceAsync(
                message,
                material,
                destination,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return RuntimeOperationResult.Accepted(
                "Material arrival was reconciled from persisted material evidence.");
        }

        return await materialService.ArriveAsync(
                new ArriveMaterialCommand(
                    message.MessageId,
                    material,
                    destination,
                    message.ActorId,
                    message.ArrivedAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<bool> HasArrivalEvidenceAsync(
        MaterialArrived message,
        MaterialReference material,
        MaterialLocation destination,
        CancellationToken cancellationToken)
    {
        var query = material.Kind switch
        {
            MaterialKind.ProductionUnit => new ProductionMaterialTimelineQuery(
                productionUnitId: material.RequireProductionUnitId(),
                throughUtc: message.ArrivedAtUtc),
            MaterialKind.Carrier => new ProductionMaterialTimelineQuery(
                carrierId: material.RequireCarrierId(),
                throughUtc: message.ArrivedAtUtc),
            _ => throw new InvalidDataException(
                $"Unsupported material arrival kind {material.Kind}.")
        };
        var timeline = await materialRepository.ListTimelineAsync(
                query,
                cancellationToken)
            .ConfigureAwait(false);
        var evidence = timeline.SingleOrDefault(entry => entry.EvidenceId == message.MessageId);
        if (evidence is null)
        {
            return false;
        }

        if (evidence.Kind != ProductionMaterialEvidenceKind.LocationTransition
            || evidence.Material != material
            || evidence.SourceLocation is not null
            || !Equals(evidence.DestinationLocation, destination)
            || evidence.OccurredAtUtc != message.ArrivedAtUtc
            || !string.Equals(evidence.ActorId, message.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Material arrival message {message.MessageId:D} evidence does not match its immutable timeline entry.");
        }

        return true;
    }

    private static MaterialReference ToMaterialReference(MaterialArrived message) =>
        message.MaterialKind switch
        {
            StationMaterialKinds.ProductionUnit => MaterialReference.ForProductionUnit(
                new ProductionUnitId(Guid.ParseExact(message.MaterialId, "D"))),
            StationMaterialKinds.Carrier => MaterialReference.ForCarrier(
                new CarrierId(message.MaterialId)),
            _ => throw new InvalidDataException(
                $"Unsupported Station material kind '{message.MaterialKind}'.")
        };

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
