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
    public async Task InMemoryStaleHoldCannotFreezeAReplacementLease()
    {
        var clock = new MutableClock(Now);
        var repository = new InMemoryResourceLeaseRepository(clock);

        await AssertStaleHoldCannotFreezeAReplacementLeaseAsync(repository, clock);
    }

    [Fact]
    public async Task SqliteStaleHoldCannotFreezeAReplacementLease()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "stale-hold.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        var clock = new MutableClock(Now);
        using var repository = new SqliteResourceLeaseRepository(builder.ToString(), clock);

        await AssertStaleHoldCannotFreezeAReplacementLeaseAsync(repository, clock);
    }

    [Fact]
    public async Task InMemoryExpiredButUnreplacedLeaseCanBeHeldForRecovery()
    {
        var clock = new MutableClock(Now);
        var repository = new InMemoryResourceLeaseRepository(clock);

        await AssertExpiredButUnreplacedLeaseCanBeHeldForRecoveryAsync(repository, clock);
    }

    [Fact]
    public async Task SqliteExpiredButUnreplacedLeaseCanBeHeldForRecovery()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "expired-hold.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        var clock = new MutableClock(Now);
        using var repository = new SqliteResourceLeaseRepository(builder.ToString(), clock);

        await AssertExpiredButUnreplacedLeaseCanBeHeldForRecoveryAsync(repository, clock);
    }

    [Fact]
    public async Task InMemoryHoldRejectsAnIncompleteOwnerSetWithoutMutation()
    {
        var repository = new InMemoryResourceLeaseRepository(new MutableClock(Now));

        await AssertHoldRejectsAnIncompleteOwnerSetWithoutMutationAsync(repository);
    }

    [Fact]
    public async Task SqliteHoldRejectsAnIncompleteOwnerSetWithoutMutation()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "incomplete-hold.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        using var repository = new SqliteResourceLeaseRepository(
            builder.ToString(),
            new MutableClock(Now));

        await AssertHoldRejectsAnIncompleteOwnerSetWithoutMutationAsync(repository);
    }

    [Fact]
    public async Task InMemoryHoldIgnoresAnotherOperationsFinitePreStartReservation()
    {
        var repository = new InMemoryResourceLeaseRepository(new MutableClock(Now));

        await AssertHoldIgnoresFinitePreStartReservationAsync(repository);
    }

    [Fact]
    public async Task SqliteHoldIgnoresAnotherOperationsFinitePreStartReservation()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "pre-start-reservation.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        using var repository = new SqliteResourceLeaseRepository(
            builder.ToString(),
            new MutableClock(Now));

        await AssertHoldIgnoresFinitePreStartReservationAsync(repository);
    }

    [Fact]
    public async Task InMemoryRecoveryHoldRequiresExactExplicitRelease()
    {
        var repository = new InMemoryResourceLeaseRepository(new MutableClock(Now));

        await AssertRecoveryHoldRequiresExactExplicitReleaseAsync(repository);
    }

    [Fact]
    public async Task SqliteRecoveryHoldRequiresExactExplicitRelease()
    {
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "recovery-release.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        };
        using var repository = new SqliteResourceLeaseRepository(
            builder.ToString(),
            new MutableClock(Now));

        await AssertRecoveryHoldRequiresExactExplicitReleaseAsync(repository);
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

    private static async Task AssertStaleHoldCannotFreezeAReplacementLeaseAsync(
        IResourceLeaseRepository repository,
        MutableClock clock)
    {
        var resource = new ResourceRequirement(ResourceKind.Device, "binding.device.hold-race");
        var firstRunId = ProductionRunId.New();
        const string firstOperationRunId = "operation.first@0001";
        var first = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                firstRunId,
                firstOperationRunId,
                [resource],
                TimeSpan.FromMinutes(1))));
        var staleHold = LeaseHold(firstOperationRunId, [first]);
        clock.UtcNow = Now.AddMinutes(2);
        var replacementRunId = ProductionRunId.New();
        var replacement = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
                await repository.TryAcquireAsync(
                    replacementRunId,
                    "operation.replacement@0001",
                    [resource],
                    TimeSpan.FromMinutes(5))));

        await Assert.ThrowsAsync<ResourceLeaseOwnershipException>(() =>
            repository.HoldForRecoveryAsync(firstRunId, [staleHold]).AsTask());

        var persisted = Assert.Single(await repository.ListAsync());
        Assert.Equal(replacement, persisted);
        Assert.NotEqual(DateTimeOffset.MaxValue, persisted.ExpiresAtUtc);
    }

    private static async Task AssertRecoveryHoldRequiresExactExplicitReleaseAsync(
        IResourceLeaseRepository repository)
    {
        var runId = ProductionRunId.New();
        const string operationRunId = "operation.recovery@0001";
        var leases = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                operationRunId,
                Resources(),
                TimeSpan.FromMinutes(5)));
        var hold = LeaseHold(operationRunId, leases);
        const string otherOperationRunId = "operation.other@0001";
        var otherLeases = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                otherOperationRunId,
                [new ResourceRequirement(ResourceKind.Fixture, "fixture.other")],
                TimeSpan.FromMinutes(5)));
        var otherHold = LeaseHold(otherOperationRunId, otherLeases);
        var totalLeaseCount = leases.Count + otherLeases.Count;

        await repository.HoldForRecoveryAsync(runId, [hold, otherHold]);
        Assert.All(await repository.ListAsync(), static lease =>
            Assert.Equal(DateTimeOffset.MaxValue, lease.ExpiresAtUtc));

        await repository.ReleaseAsync(
            runId,
            operationRunId,
            leases.Select(ResourceLeaseReleaseClaim.FromLease).ToArray());
        Assert.Equal(totalLeaseCount, (await repository.ListAsync()).Count);

        var staleClaims = hold.Claims
            .Select((claim, index) => new ResourceLeaseHoldClaim(
                claim.Resource,
                index == 0 ? checked(claim.FencingToken + 1) : claim.FencingToken))
            .ToArray();
        var staleHold = new ProductionRunLeaseHold(operationRunId, staleClaims);
        await Assert.ThrowsAsync<ResourceLeaseOwnershipException>(() =>
            repository.ReleaseRecoveryHoldAsync(runId, [staleHold]).AsTask());
        Assert.Equal(totalLeaseCount, (await repository.ListAsync()).Count);

        await repository.ReleaseRecoveryHoldAsync(runId, [hold]);
        var remaining = Assert.Single(await repository.ListAsync());
        Assert.Equal(otherOperationRunId, remaining.OperationRunId);
        Assert.Equal(DateTimeOffset.MaxValue, remaining.ExpiresAtUtc);

        await repository.ReleaseRecoveryHoldAsync(runId, [otherHold]);
        Assert.Empty(await repository.ListAsync());
    }

    private static async Task AssertExpiredButUnreplacedLeaseCanBeHeldForRecoveryAsync(
        IResourceLeaseRepository repository,
        MutableClock clock)
    {
        var runId = ProductionRunId.New();
        const string operationRunId = "operation.expired@0001";
        var resource = new ResourceRequirement(ResourceKind.Device, "binding.device.expired");
        var lease = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                operationRunId,
                [resource],
                TimeSpan.FromMinutes(1))));
        clock.UtcNow = Now.AddMinutes(2);

        await repository.HoldForRecoveryAsync(
            runId,
            [LeaseHold(operationRunId, [lease])]);

        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(await repository.ListAsync()).ExpiresAtUtc);
        Assert.Null(await repository.TryAcquireAsync(
            ProductionRunId.New(),
            "operation.replacement@0001",
            [resource],
            TimeSpan.FromMinutes(1)));
    }

    private static async Task AssertHoldRejectsAnIncompleteOwnerSetWithoutMutationAsync(
        IResourceLeaseRepository repository)
    {
        var runId = ProductionRunId.New();
        const string operationRunId = "operation.incomplete@0001";
        var leases = Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                operationRunId,
                Resources(),
                TimeSpan.FromMinutes(5)));
        var omittedLease = leases.OrderBy(static lease => lease.Resource.CanonicalKey).Last();
        var incomplete = LeaseHold(
            operationRunId,
            leases.Where(lease => lease != omittedLease));

        await Assert.ThrowsAsync<ResourceLeaseOwnershipException>(() =>
            repository.HoldForRecoveryAsync(runId, [incomplete]).AsTask());

        var persisted = await repository.ListAsync();
        Assert.Equal(leases.Count, persisted.Count);
        Assert.All(persisted, static lease =>
            Assert.NotEqual(DateTimeOffset.MaxValue, lease.ExpiresAtUtc));
    }

    private static async Task AssertHoldIgnoresFinitePreStartReservationAsync(
        IResourceLeaseRepository repository)
    {
        var runId = ProductionRunId.New();
        const string durableOperationRunId = "operation.durable@0001";
        var durable = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                durableOperationRunId,
                [new ResourceRequirement(ResourceKind.Station, "station.durable")],
                TimeSpan.FromMinutes(5))));
        const string preStartOperationRunId = "operation.pre-start@0001";
        var preStart = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<ResourceLease>>(
            await repository.TryAcquireAsync(
                runId,
                preStartOperationRunId,
                [new ResourceRequirement(ResourceKind.Device, "device.pre-start")],
                TimeSpan.FromMinutes(5))));

        await repository.HoldForRecoveryAsync(
            runId,
            [LeaseHold(durableOperationRunId, [durable])]);

        var persisted = (await repository.ListAsync())
            .ToDictionary(static lease => lease.OperationRunId, StringComparer.Ordinal);
        Assert.Equal(DateTimeOffset.MaxValue, persisted[durableOperationRunId].ExpiresAtUtc);
        Assert.Equal(preStart.ExpiresAtUtc, persisted[preStartOperationRunId].ExpiresAtUtc);
        Assert.NotEqual(DateTimeOffset.MaxValue, persisted[preStartOperationRunId].ExpiresAtUtc);
    }

    private static ProductionRunLeaseHold LeaseHold(
        string operationRunId,
        IEnumerable<ResourceLease> leases) =>
        new(
            operationRunId,
            leases.Select(ResourceLeaseHoldClaim.FromLease).ToArray());

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
