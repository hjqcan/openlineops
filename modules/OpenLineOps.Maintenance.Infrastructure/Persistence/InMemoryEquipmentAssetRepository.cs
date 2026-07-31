using OpenLineOps.Maintenance.Application.Persistence;
using OpenLineOps.Maintenance.Domain.Assets;
using OpenLineOps.Maintenance.Domain.Identifiers;

namespace OpenLineOps.Maintenance.Infrastructure.Persistence;

public sealed class InMemoryEquipmentAssetRepository : IEquipmentAssetRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<EquipmentAssetId, StoredAsset> _assets = [];

    public ValueTask<EquipmentAssetAddResult> TryAddAsync(
        EquipmentAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_assets.ContainsKey(asset.Id))
            {
                return ValueTask.FromResult(
                    EquipmentAssetAddResult.AlreadyExists);
            }

            var facts = asset.Facts.ToArray();
            _assets.Add(asset.Id, new StoredAsset(facts, facts.LongLength));
            return ValueTask.FromResult(EquipmentAssetAddResult.Added);
        }
    }

    public ValueTask<EquipmentAssetPersistenceEntry?> GetByIdAsync(
        EquipmentAssetId assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _assets.TryGetValue(assetId, out var stored)
                    ? ToEntry(stored)
                    : null);
        }
    }

    public ValueTask<IReadOnlyCollection<EquipmentAssetPersistenceEntry>>
        ListByStationAsync(
            string stationId,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyCollection<EquipmentAssetPersistenceEntry> entries =
                _assets.Values
                    .Select(ToEntry)
                    .Where(
                        entry => string.Equals(
                            entry.Asset.StationId,
                            stationId,
                            StringComparison.Ordinal))
                    .OrderBy(
                        static entry => entry.Asset.Id.Value,
                        StringComparer.Ordinal)
                    .ToArray();
            return ValueTask.FromResult(entries);
        }
    }

    public ValueTask<long> SaveAsync(
        EquipmentAsset asset,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_assets.TryGetValue(asset.Id, out var stored)
                || stored.Revision != expectedRevision)
            {
                throw new EquipmentAssetConcurrencyException(
                    asset.Id,
                    expectedRevision);
            }

            var facts = asset.Facts.ToArray();
            if (facts.LongLength < expectedRevision
                || !stored.Facts.SequenceEqual(
                    facts.Take(checked((int)expectedRevision))))
            {
                throw new InvalidDataException(
                    $"Equipment asset {asset.Id} does not preserve its stored fact prefix.");
            }

            var revision = facts.LongLength;
            _assets[asset.Id] = new StoredAsset(facts, revision);
            return ValueTask.FromResult(revision);
        }
    }

    private static EquipmentAssetPersistenceEntry ToEntry(StoredAsset stored) =>
        new(EquipmentAsset.Restore(stored.Facts), stored.Revision);

    private sealed record StoredAsset(
        IReadOnlyCollection<EquipmentFact> Facts,
        long Revision);
}
