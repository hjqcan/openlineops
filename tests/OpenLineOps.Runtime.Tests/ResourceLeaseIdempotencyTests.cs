using Microsoft.Data.Sqlite;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ResourceLeaseIdempotencyTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-lease-idempotency",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InMemorySameOwnerExactSetReturnsOriginalFencesAndPartialSetFailsClosed()
    {
        var repository = new InMemoryResourceLeaseRepository();
        await AssertSameOwnerSemanticsAsync(repository);
    }

    [Fact]
    public async Task SqliteConcurrentInstancesReturnOneCanonicalFenceSet()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "leases.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        using var firstRepository = new SqliteResourceLeaseRepository(builder.ToString());
        using var secondRepository = new SqliteResourceLeaseRepository(builder.ToString());
        var runId = ProductionRunId.New();
        const string operationRunId = "operation.main@0001";
        var resources = Resources();

        var results = await Task.WhenAll(
            AcquireAsync(firstRepository, runId, operationRunId, resources),
            AcquireAsync(secondRepository, runId, operationRunId, resources));

        Assert.All(results, Assert.NotNull);
        Assert.Equal(
            Canonical(results[0]!),
            Canonical(results[1]!));
        Assert.All(results.SelectMany(static result => result!), lease =>
            Assert.Equal(Now, lease.AcquiredAtUtc));
        Assert.Null(await secondRepository.TryAcquireAsync(
            runId,
            operationRunId,
            [resources[0]],
            Now.AddSeconds(2),
            TimeSpan.FromMinutes(10)));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static async Task AssertSameOwnerSemanticsAsync(
        InMemoryResourceLeaseRepository repository)
    {
        var runId = ProductionRunId.New();
        const string operationRunId = "operation.main@0001";
        var resources = Resources();
        var first = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                operationRunId,
                resources,
                Now,
                TimeSpan.FromMinutes(10)));
        var retry = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                operationRunId,
                resources.Reverse().ToArray(),
                Now.AddSeconds(1),
                TimeSpan.FromHours(1)));

        Assert.Equal(Canonical(first), Canonical(retry));
        Assert.Null(await repository.TryAcquireAsync(
            runId,
            operationRunId,
            [resources[0]],
            Now.AddSeconds(2),
            TimeSpan.FromMinutes(10)));
    }

    private static async Task<IReadOnlyCollection<ResourceLease>?> AcquireAsync(
        SqliteResourceLeaseRepository repository,
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources) =>
        await repository.TryAcquireAsync(
            runId,
            operationRunId,
            resources,
            Now,
            TimeSpan.FromMinutes(10));

    private static ResourceRequirement[] Resources() =>
    [
        new(ResourceKind.Station, "station.main"),
        new(ResourceKind.Device, "binding.device.main")
    ];

    private static string[] Canonical(IReadOnlyCollection<ResourceLease> leases) => leases
        .OrderBy(static lease => lease.Resource.CanonicalKey, StringComparer.Ordinal)
        .Select(static lease =>
            $"{lease.Resource.CanonicalKey}|{lease.FencingToken}|"
            + $"{lease.AcquiredAtUtc:O}|{lease.ExpiresAtUtc:O}")
        .ToArray();
}
