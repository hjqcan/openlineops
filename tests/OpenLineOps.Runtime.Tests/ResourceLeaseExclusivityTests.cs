using Microsoft.Data.Sqlite;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class ResourceLeaseExclusivityTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-lease-exclusivity",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InMemoryActiveLeaseIsExclusiveEvenForSameOwner()
    {
        var repository = new InMemoryResourceLeaseRepository(new MutableClock(Now));
        await AssertExclusiveOwnerSemanticsAsync(repository);
    }

    [Fact]
    public async Task SqliteConcurrentInstancesAllowOnlyOneAcquisitionAttempt()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "leases.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        var clock = new MutableClock(Now);
        using var firstRepository = new SqliteResourceLeaseRepository(builder.ToString(), clock);
        using var secondRepository = new SqliteResourceLeaseRepository(builder.ToString(), clock);
        var runId = ProductionRunId.New();
        const string operationRunId = "operation.main@0001";
        var resources = Resources();

        var results = await Task.WhenAll(
            AcquireAsync(firstRepository, runId, operationRunId, resources),
            AcquireAsync(secondRepository, runId, operationRunId, resources));

        var acquired = Assert.Single(results, static result => result is not null)!;
        Assert.Single(results, static result => result is null);
        Assert.All(acquired, lease => Assert.Equal(Now, lease.AcquiredAtUtc));
        Assert.Null(await secondRepository.TryAcquireAsync(
            runId,
            operationRunId,
            [resources[0]],
            TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task SqliteStaleReleaseClaimCannotDeleteAReplacementLease()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "stale-release.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        var clock = new MutableClock(Now);
        using var repository = new SqliteResourceLeaseRepository(builder.ToString(), clock);
        var resource = new ResourceRequirement(ResourceKind.Device, "binding.device.stale");
        var firstRunId = ProductionRunId.New();
        var replacementRunId = ProductionRunId.New();
        var first = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                firstRunId,
                "operation.first@0001",
                [resource],
                TimeSpan.FromMinutes(1))));
        clock.UtcNow = Now.AddMinutes(2);
        var replacement = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                await repository.TryAcquireAsync(
                    replacementRunId,
                    "operation.replacement@0001",
                    [resource],
                    TimeSpan.FromMinutes(5))));

        await repository.ReleaseAsync(
            firstRunId,
            first.OperationRunId,
            [ResourceLeaseReleaseClaim.FromLease(first)]);

        Assert.Equal(replacement, Assert.Single(await repository.ListAsync()));
        clock.UtcNow = Now.AddMinutes(3);
        Assert.Null(await repository.TryAcquireAsync(
            ProductionRunId.New(),
            "operation.third@0001",
            [resource],
            TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task InMemoryStoreClockDeterminesFenceExpiry()
    {
        var clock = new MutableClock(Now);
        var repository = new InMemoryResourceLeaseRepository(clock);

        await AssertStoreClockDeterminesFenceExpiryAsync(repository, clock);
    }

    [Fact]
    public async Task SqliteStoreClockDeterminesFenceExpiry()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "validation-clock.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        var clock = new MutableClock(Now);
        using var repository = new SqliteResourceLeaseRepository(builder.ToString(), clock);

        await AssertStoreClockDeterminesFenceExpiryAsync(repository, clock);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static async Task AssertExclusiveOwnerSemanticsAsync(
        InMemoryResourceLeaseRepository repository)
    {
        var runId = ProductionRunId.New();
        const string operationRunId = "operation.main@0001";
        var resources = Resources();
        Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                operationRunId,
                resources,
                TimeSpan.FromMinutes(10)));
        Assert.Null(await repository.TryAcquireAsync(
            runId,
            operationRunId,
            resources.Reverse().ToArray(),
            TimeSpan.FromHours(1)));
        Assert.Null(await repository.TryAcquireAsync(
            runId,
            operationRunId,
            [resources[0]],
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
            TimeSpan.FromMinutes(10));

    private static async Task AssertStoreClockDeterminesFenceExpiryAsync(
        IResourceLeaseRepository repository,
        MutableClock clock)
    {
        var runId = ProductionRunId.New();
        const string operationRunId = "operation.validation@0001";
        var resource = new ResourceRequirement(
            ResourceKind.Slot,
            "line.validation/station.validation/slot.validation");
        var lease = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                operationRunId,
                [resource],
                TimeSpan.FromMinutes(1))));
        var evidence = new[] { ResourceLeaseFenceEvidence.FromLease(lease) };

        Assert.True((await repository.ValidateCurrentAsync(
            runId,
            operationRunId,
            evidence)).Accepted);
        clock.UtcNow = Now.AddMinutes(2);
        Assert.False((await repository.ValidateCurrentAsync(
            runId,
            operationRunId,
            evidence)).Accepted);
    }

    private static ResourceRequirement[] Resources() =>
    [
        new(ResourceKind.Station, "station.main"),
        new(ResourceKind.Device, "binding.device.main")
    ];

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
