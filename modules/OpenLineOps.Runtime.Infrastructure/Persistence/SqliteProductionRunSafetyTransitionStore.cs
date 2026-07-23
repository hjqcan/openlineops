using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteProductionRunSafetyTransitionStore : IProductionRunSafetyTransitionStore
{
    private readonly SqliteProductionRunRepository _productionRuns;
    private readonly SqliteResourceLeaseRepository _resourceLeases;

    public SqliteProductionRunSafetyTransitionStore(
        SqliteProductionRunRepository productionRuns,
        SqliteResourceLeaseRepository resourceLeases)
    {
        _productionRuns = productionRuns
            ?? throw new ArgumentNullException(nameof(productionRuns));
        _resourceLeases = resourceLeases
            ?? throw new ArgumentNullException(nameof(resourceLeases));
        if (!SharesFileBackedDatabase(
                _productionRuns.ConnectionString,
                _resourceLeases.ConnectionString))
        {
            throw new ArgumentException(
                "SQLite Production Run and resource lease repositories must share one database.",
                nameof(resourceLeases));
        }
    }

    private static bool SharesFileBackedDatabase(string left, string right)
    {
        var leftBuilder = new SqliteConnectionStringBuilder(left);
        var rightBuilder = new SqliteConnectionStringBuilder(right);
        if (IsMemoryDatabase(leftBuilder) || IsMemoryDatabase(rightBuilder))
        {
            return false;
        }

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
                NormalizeDataSource(leftBuilder.DataSource),
                NormalizeDataSource(rightBuilder.DataSource),
                pathComparison)
            && leftBuilder.Mode == rightBuilder.Mode
            && leftBuilder.Cache == rightBuilder.Cache;
    }

    private static bool IsMemoryDatabase(SqliteConnectionStringBuilder builder) =>
        builder.Mode == SqliteOpenMode.Memory
        || builder.DataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
        || builder.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        && builder.DataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDataSource(string dataSource) =>
        dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? dataSource
            : Path.GetFullPath(dataSource);

    public async ValueTask<long> SaveWithLeaseHoldsAsync(
        ProductionRun run,
        long expectedRevision,
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
        CancellationToken cancellationToken = default)
    {
        var canonicalHolds = ProductionRunLeaseHold.RequireExactFor(run, leaseHolds);
        var expected = ProductionRunLeaseHoldSet.Create(canonicalHolds);
        await _productionRuns.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await _resourceLeases.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = _productionRuns.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await SqliteResourceLeaseRepository.HoldForRecoveryInTransactionAsync(
                    connection,
                    transaction,
                    run.Id,
                    expected,
                    cancellationToken)
                .ConfigureAwait(false);
            var revision = await SqliteProductionRunRepository.SaveInTransactionAsync(
                    connection,
                    transaction,
                    run,
                    expectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return revision;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
