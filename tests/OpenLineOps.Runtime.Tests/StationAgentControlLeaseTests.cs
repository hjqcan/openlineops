using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Stations;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Infrastructure.Persistence;

namespace OpenLineOps.Runtime.Tests;

public sealed class StationAgentControlLeaseTests
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private const string InstanceA = "11111111-1111-4111-8111-111111111111";
    private const string InstanceB = "22222222-2222-4222-8222-222222222222";
    private static readonly string LeaseHandleA = CreateLeaseHandle(0x11);
    private static readonly string LeaseHandleB = CreateLeaseHandle(0x22);
    private static readonly string LeaseProofA = HashLeaseHandle(LeaseHandleA);
    private static readonly string LeaseProofB = HashLeaseHandle(LeaseHandleB);

    [Fact]
    public async Task InMemoryLeaseRequiresExactOwnerAndTokenAndFencesTakeover()
    {
        var clock = new MutableClock(BaseTimeUtc);
        var repository = new InMemoryStationAgentControlLeaseRepository(clock);

        await VerifyOwnershipAndTakeoverAsync(repository, clock);
    }

    [Fact]
    public async Task SqliteLeasePreservesTokenHighWaterAcrossColdStarts()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "runtime.sqlite");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            var clock = new MutableClock(BaseTimeUtc);
            var stationId = new StationId("station-a");

            long firstToken;
            using (var first = new SqliteStationAgentControlLeaseRepository(
                       connectionString,
                       clock))
            {
                var acquired = await first.TryAcquireAsync(
                    stationId,
                    AgentA,
                    InstanceA,
                    LeaseProofA,
                    TimeSpan.FromSeconds(10));
                Assert.Equal(
                    StationAgentControlLeaseMutationStatus.Acquired,
                    acquired.Status);
                firstToken = acquired.Lease!.FencingToken;
            }

            clock.UtcNow = BaseTimeUtc.AddSeconds(11);
            long secondToken;
            using (var restarted = new SqliteStationAgentControlLeaseRepository(
                       connectionString,
                       clock))
            {
                var takeover = await restarted.TryAcquireAsync(
                    stationId,
                    AgentB,
                    InstanceB,
                    LeaseProofB,
                    TimeSpan.FromSeconds(10));
                Assert.Equal(
                    StationAgentControlLeaseMutationStatus.Acquired,
                    takeover.Status);
                secondToken = takeover.Lease!.FencingToken;
                Assert.True(secondToken > firstToken);

                var oldFence = await restarted.ValidateProofAsync(
                    stationId,
                    AgentA,
                    InstanceA,
                    firstToken,
                    LeaseProofA);
                Assert.False(oldFence.IsValid);

                clock.UtcNow = BaseTimeUtc.AddSeconds(12);
                var renewed = await restarted.TryRenewAsync(
                    stationId,
                    AgentB,
                    InstanceB,
                    secondToken,
                    LeaseProofB,
                    TimeSpan.FromSeconds(10));
                Assert.Equal(
                    StationAgentControlLeaseMutationStatus.Renewed,
                    renewed.Status);
            }

            clock.UtcNow = BaseTimeUtc.AddSeconds(13);
            using (var secondRestart =
                   new SqliteStationAgentControlLeaseRepository(
                       connectionString,
                       clock))
            {
                var restored = await secondRestart.GetAsync(stationId);
                Assert.True(restored.IsActive);
                Assert.Equal(secondToken, restored.Lease!.FencingToken);
                Assert.Equal(AgentB, restored.Lease.OwnerAgentId);
                Assert.Equal(InstanceB, restored.Lease.OwnerInstanceId);

                var released = await secondRestart.TryReleaseAsync(
                    stationId,
                    AgentB,
                    InstanceB,
                    secondToken,
                    LeaseProofB);
                Assert.Equal(
                    StationAgentControlLeaseMutationStatus.Released,
                    released.Status);
                Assert.False((await secondRestart.ValidateProofAsync(
                    stationId,
                    AgentB,
                    InstanceB,
                    secondToken,
                    LeaseProofB)).IsValid);

                var reacquired = await secondRestart.TryAcquireAsync(
                    stationId,
                    AgentA,
                    InstanceA,
                    LeaseProofA,
                    TimeSpan.FromSeconds(10));
                Assert.True(reacquired.Lease!.FencingToken > secondToken);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteConcurrentAcquireHasExactlyOneOwner()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "runtime.sqlite"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                DefaultTimeout = 5,
                Pooling = false
            }.ToString();
            var clock = new MutableClock(BaseTimeUtc);
            var stationId = new StationId("station-a");
            using var left = new SqliteStationAgentControlLeaseRepository(
                connectionString,
                clock);
            using var right = new SqliteStationAgentControlLeaseRepository(
                connectionString,
                clock);
            _ = await left.GetAsync(stationId);
            _ = await right.GetAsync(stationId);

            var results = await Task.WhenAll(
                left.TryAcquireAsync(
                    stationId,
                    AgentA,
                    InstanceA,
                    LeaseProofA,
                    TimeSpan.FromSeconds(10)).AsTask(),
                right.TryAcquireAsync(
                    stationId,
                    AgentB,
                    InstanceB,
                    LeaseProofB,
                    TimeSpan.FromSeconds(10)).AsTask());

            Assert.Single(results, static result =>
                result.Status == StationAgentControlLeaseMutationStatus.Acquired);
            Assert.Single(results, static result =>
                result.Status
                    == StationAgentControlLeaseMutationStatus.HeldByAnotherOwner);
            Assert.All(results, static result =>
                Assert.Equal(1, result.Lease!.FencingToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SameAgentProcessCannotReplaceAnActiveLeaseHandle()
    {
        var clock = new MutableClock(BaseTimeUtc);
        var repository = new InMemoryStationAgentControlLeaseRepository(clock);
        var service = new StationAgentControlLeaseService(
            repository,
            new StationAgentControlLeaseOptions
            {
                TimeToLive = TimeSpan.FromSeconds(10)
            });
        var stationId = new StationId("station-stale-handle");

        var acquired = await service.AcquireAsync(
            stationId,
            AgentA,
            InstanceA,
            LeaseHandleA);
        var staleRetry = await service.AcquireAsync(
            stationId,
            AgentA,
            InstanceA,
            LeaseHandleB);

        Assert.True(acquired.IsSuccess);
        Assert.True(staleRetry.IsFailure);
        Assert.Equal(
            "Conflict.Runtime.StationAgentControlLeaseStaleProof",
            staleRetry.Error.Code);
        Assert.False((await service.ValidateAsync(
            stationId,
            AgentA,
            InstanceA,
            acquired.Value.Observation.Lease!.FencingToken,
            LeaseHandleB)).IsValid);
        Assert.True((await service.ValidateAsync(
            stationId,
            AgentA,
            InstanceA,
            acquired.Value.Observation.Lease.FencingToken,
            LeaseHandleA)).IsValid);
    }

    [Fact]
    public async Task InMemoryClockCannotMoveBehindReleaseTimestamp()
    {
        var stationId = new StationId("station-a");
        var clock = new MutableClock(BaseTimeUtc);
        var repository = new InMemoryStationAgentControlLeaseRepository(clock);
        _ = await repository.TryAcquireAsync(
            stationId,
            AgentA,
            InstanceA,
            LeaseProofA,
            TimeSpan.FromSeconds(10));

        clock.UtcNow = BaseTimeUtc.AddSeconds(5);
        var released = await repository.TryReleaseAsync(
            stationId,
            AgentA,
            InstanceA,
            fencingToken: 1,
            LeaseProofA);
        Assert.Equal(
            StationAgentControlLeaseMutationStatus.Released,
            released.Status);
        Assert.Equal(released.Lease!.RenewedAtUtc, released.Lease.ExpiresAtUtc);

        clock.UtcNow = BaseTimeUtc.AddSeconds(3);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.GetAsync(stationId));

        clock.UtcNow = BaseTimeUtc.AddSeconds(5).ToOffset(TimeSpan.FromHours(8));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.GetAsync(stationId));
    }

    [Fact]
    public async Task SqliteReleaseTimestampRejectsClockRollbackAfterColdStart()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "runtime.sqlite"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();
            var stationId = new StationId("station-release-clock");
            var clock = new MutableClock(BaseTimeUtc);
            using (var repository = new SqliteStationAgentControlLeaseRepository(
                       connectionString,
                       clock))
            {
                _ = await repository.TryAcquireAsync(
                    stationId,
                    AgentA,
                    InstanceA,
                    LeaseProofA,
                    TimeSpan.FromSeconds(10));
                clock.UtcNow = BaseTimeUtc.AddSeconds(5);
                var released = await repository.TryReleaseAsync(
                    stationId,
                    AgentA,
                    InstanceA,
                    fencingToken: 1,
                    LeaseProofA);
                Assert.Equal(
                    StationAgentControlLeaseMutationStatus.Released,
                    released.Status);
                Assert.Equal(
                    BaseTimeUtc.AddSeconds(5),
                    released.Lease!.RenewedAtUtc);
            }

            clock.UtcNow = BaseTimeUtc.AddSeconds(3);
            using var restarted = new SqliteStationAgentControlLeaseRepository(
                connectionString,
                clock);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await restarted.GetAsync(stationId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task VerifyOwnershipAndTakeoverAsync(
        InMemoryStationAgentControlLeaseRepository repository,
        MutableClock clock)
    {
        var stationId = new StationId("station-a");
        var duration = TimeSpan.FromSeconds(10);
        var acquired = await repository.TryAcquireAsync(
            stationId,
            AgentA,
            InstanceA,
            LeaseProofA,
            duration);
        Assert.Equal(StationAgentControlLeaseMutationStatus.Acquired, acquired.Status);
        Assert.Equal(1, acquired.Lease!.FencingToken);
        var originalExpiry = acquired.Lease.ExpiresAtUtc;

        clock.UtcNow = BaseTimeUtc.AddSeconds(1);
        var retry = await repository.TryAcquireAsync(
            stationId,
            AgentA,
            InstanceA,
            LeaseProofA,
            duration);
        Assert.Equal(
            StationAgentControlLeaseMutationStatus.AlreadyOwned,
            retry.Status);
        Assert.Equal(1, retry.Lease!.FencingToken);
        Assert.Equal(originalExpiry, retry.Lease.ExpiresAtUtc);

        var contended = await repository.TryAcquireAsync(
            stationId,
            AgentB,
            InstanceB,
            LeaseProofB,
            duration);
        Assert.Equal(
            StationAgentControlLeaseMutationStatus.HeldByAnotherOwner,
            contended.Status);

        var wrongToken = await repository.TryRenewAsync(
            stationId,
            AgentA,
            InstanceA,
            fencingToken: 2,
            LeaseProofA,
            duration);
        Assert.Equal(
            StationAgentControlLeaseMutationStatus.StaleFencingToken,
            wrongToken.Status);

        var wrongOwner = await repository.TryRenewAsync(
            stationId,
            AgentB,
            InstanceB,
            fencingToken: 1,
            LeaseProofB,
            duration);
        Assert.Equal(
            StationAgentControlLeaseMutationStatus.StaleOwner,
            wrongOwner.Status);

        clock.UtcNow = BaseTimeUtc.AddSeconds(2);
        var renewed = await repository.TryRenewAsync(
            stationId,
            AgentA,
            InstanceA,
            fencingToken: 1,
            LeaseProofA,
            duration);
        Assert.Equal(StationAgentControlLeaseMutationStatus.Renewed, renewed.Status);
        Assert.Equal(BaseTimeUtc.AddSeconds(12), renewed.Lease!.ExpiresAtUtc);

        clock.UtcNow = BaseTimeUtc.AddSeconds(13);
        Assert.False((await repository.ValidateProofAsync(
            stationId,
            AgentA,
            InstanceA,
            fencingToken: 1,
            LeaseProofA)).IsValid);

        var takeover = await repository.TryAcquireAsync(
            stationId,
            AgentB,
            InstanceB,
            LeaseProofB,
            duration);
        Assert.Equal(StationAgentControlLeaseMutationStatus.Acquired, takeover.Status);
        Assert.Equal(2, takeover.Lease!.FencingToken);
        Assert.False((await repository.ValidateProofAsync(
            stationId,
            AgentA,
            InstanceA,
            fencingToken: 1,
            LeaseProofA)).IsValid);
        Assert.True((await repository.ValidateProofAsync(
            stationId,
            AgentB,
            InstanceB,
            fencingToken: 2,
            LeaseProofB)).IsValid);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "openlineops-agent-control-lease-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateLeaseHandle(byte value) =>
        Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string HashLeaseHandle(string leaseHandle) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.ASCII.GetBytes(leaseHandle)))
            .ToLowerInvariant();

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
