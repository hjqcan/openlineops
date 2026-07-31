using OpenLineOps.Maintenance.Application.Persistence;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Domain.Identifiers;

namespace OpenLineOps.Maintenance.Tests;

internal sealed class ThrowingEquipmentAssetRepository :
    IEquipmentAssetRepository
{
    public ValueTask<EquipmentAssetAddResult> TryAddAsync(
        EquipmentAsset asset,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Repository must not be called.");

    public ValueTask<EquipmentAssetPersistenceEntry?> GetByIdAsync(
        EquipmentAssetId assetId,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Repository must not be called.");

    public ValueTask<IReadOnlyCollection<EquipmentAssetPersistenceEntry>>
        ListByStationAsync(
            string stationId,
            CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Repository must not be called.");

    public ValueTask<long> SaveAsync(
        EquipmentAsset asset,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Repository must not be called.");
}
