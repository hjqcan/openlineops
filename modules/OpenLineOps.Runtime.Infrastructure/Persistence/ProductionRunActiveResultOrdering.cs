using OpenLineOps.Runtime.Application.Persistence;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class ProductionRunActiveResultOrdering
{
    public static ProductionRunPersistenceEntry[] Apply(
        IEnumerable<ProductionRunPersistenceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .OrderByDescending(static entry => entry.Run.LastTransitionAtUtc)
            .ThenBy(static entry => entry.Run.Id.Value)
            .ToArray();
    }
}
