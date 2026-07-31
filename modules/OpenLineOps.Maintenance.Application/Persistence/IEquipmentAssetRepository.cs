using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Domain.Identifiers;

namespace OpenLineOps.Maintenance.Application.Persistence;

public interface IEquipmentAssetRepository
{
    ValueTask<EquipmentAssetAddResult> TryAddAsync(
        EquipmentAsset asset,
        CancellationToken cancellationToken = default);

    ValueTask<EquipmentAssetPersistenceEntry?> GetByIdAsync(
        EquipmentAssetId assetId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<EquipmentAssetPersistenceEntry>> ListByStationAsync(
        string stationId,
        CancellationToken cancellationToken = default);

    ValueTask<long> SaveAsync(
        EquipmentAsset asset,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed record EquipmentAssetPersistenceEntry(
    EquipmentAsset Asset,
    long Revision);

public enum EquipmentAssetAddResult
{
    Added = 0,
    AlreadyExists = 1
}

public sealed class EquipmentAssetConcurrencyException : InvalidOperationException
{
    public EquipmentAssetConcurrencyException(
        EquipmentAssetId assetId,
        long expectedRevision)
        : base(
            $"Equipment asset {assetId} was not stored at expected revision "
            + $"{expectedRevision}; reload the asset before applying another operation.")
    {
        ArgumentNullException.ThrowIfNull(assetId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        AssetId = assetId;
        ExpectedRevision = expectedRevision;
    }

    public EquipmentAssetId AssetId { get; }

    public long ExpectedRevision { get; }
}
