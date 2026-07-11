using Microsoft.Data.Sqlite;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Tests;

public sealed class StationSafetyCommandCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EmergencyStopDuplicateAfterRestartReturnsOriginalAcknowledgementWithoutActuatorReplay()
    {
        var path = DatabasePath();
        var executionCount = 0;
        var request = EmergencyStop();
        try
        {
            EmergencyStopAcknowledged first;
            using (var store = Store(path))
            {
                var coordinator = new StationSafetyCommandCoordinator(store, new FixedClock(Now));
                first = await coordinator.HandleEmergencyStopAsync(
                    request,
                    (_, _) =>
                    {
                        executionCount++;
                        return ValueTask.FromResult(new StationSafetyExecutionResult(true, null, null));
                    });
            }

            using (var restartedStore = Store(path))
            {
                var restarted = new StationSafetyCommandCoordinator(
                    restartedStore,
                    new FixedClock(Now.AddHours(1)));
                var duplicate = await restarted.HandleEmergencyStopAsync(
                    request with { MessageId = Guid.NewGuid() },
                    (_, _) => throw new InvalidOperationException("The actuator must not be replayed."));

                Assert.Equal(first, duplicate);
                Assert.Equal(request.MessageId, duplicate.RequestMessageId);
                Assert.Equal(1, executionCount);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task SafeStopDuplicateAfterRestartReturnsOriginalAcknowledgementWithoutActuatorReplay()
    {
        var path = DatabasePath();
        var executionCount = 0;
        var request = SafeStop();
        try
        {
            StationSafeStopAcknowledged first;
            using (var store = Store(path))
            {
                var coordinator = new StationSafetyCommandCoordinator(store, new FixedClock(Now));
                first = await coordinator.HandleSafeStopAsync(
                    request,
                    (_, _) =>
                    {
                        executionCount++;
                        return ValueTask.FromResult(new StationSafetyExecutionResult(
                            false,
                            "Agent.SafeStopRejected",
                            "The station safety controller rejected the request."));
                    });
            }

            using (var restartedStore = Store(path))
            {
                var restarted = new StationSafetyCommandCoordinator(
                    restartedStore,
                    new FixedClock(Now.AddHours(1)));
                var duplicate = await restarted.HandleSafeStopAsync(
                    request with { MessageId = Guid.NewGuid() },
                    (_, _) => throw new InvalidOperationException("The actuator must not be replayed."));

                Assert.Equal(first, duplicate);
                Assert.Equal(request.MessageId, duplicate.RequestMessageId);
                Assert.Equal(1, executionCount);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task PendingSafetyCommandAfterCrashRequiresReconciliationWithoutActuatorReplay()
    {
        var path = DatabasePath();
        var request = EmergencyStop();
        var executionCount = 0;
        try
        {
            using (var store = Store(path))
            {
                var coordinator = new StationSafetyCommandCoordinator(store, new FixedClock(Now));
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await coordinator.HandleEmergencyStopAsync(
                        request,
                        (_, _) =>
                        {
                            executionCount++;
                            throw new InvalidOperationException("Simulated process loss after dispatch.");
                        }));
            }

            using (var restartedStore = Store(path))
            {
                var restarted = new StationSafetyCommandCoordinator(
                    restartedStore,
                    new FixedClock(Now.AddMinutes(5)));
                var acknowledgement = await restarted.HandleEmergencyStopAsync(
                    request with { MessageId = Guid.NewGuid() },
                    (_, _) => throw new InvalidOperationException("The actuator must not be replayed."));

                Assert.False(acknowledgement.Accepted);
                Assert.Equal("Agent.SafetyRecoveryRequired", acknowledgement.FailureCode);
                Assert.Contains("not replayed", acknowledgement.FailureReason, StringComparison.Ordinal);
                Assert.Equal(request.MessageId, acknowledgement.RequestMessageId);
                Assert.Equal(1, executionCount);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task ReusedSafetyIdempotencyKeyWithDifferentEvidenceIsRejectedWithoutActuatorReplay()
    {
        var path = DatabasePath();
        var request = SafeStop();
        var executionCount = 0;
        try
        {
            using var store = Store(path);
            var coordinator = new StationSafetyCommandCoordinator(store, new FixedClock(Now));
            _ = await coordinator.HandleSafeStopAsync(
                request,
                (_, _) =>
                {
                    executionCount++;
                    return ValueTask.FromResult(new StationSafetyExecutionResult(true, null, null));
                });

            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await coordinator.HandleSafeStopAsync(
                    request with { MessageId = Guid.NewGuid(), Reason = "A different reason" },
                    (_, _) => throw new InvalidOperationException("The actuator must not be replayed.")));

            Assert.Contains("reused with different evidence", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, executionCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task JobCancellationDuplicateAfterRestartReplaysPersistedAcknowledgement()
    {
        var path = DatabasePath();
        var request = JobCancel();
        var executionCount = 0;
        try
        {
            StationJobCancelAcknowledged first;
            using (var store = Store(path))
            {
                var coordinator = new StationSafetyCommandCoordinator(store, new FixedClock(Now));
                first = await coordinator.HandleJobCancelAsync(
                    request,
                    (_, _) =>
                    {
                        executionCount++;
                        return ValueTask.FromResult(StationJobCancelExecutionResult.Success());
                    });
            }

            using (var store = Store(path))
            {
                var restarted = new StationSafetyCommandCoordinator(
                    store,
                    new FixedClock(Now.AddMinutes(1)));
                var duplicate = await restarted.HandleJobCancelAsync(
                    request with
                    {
                        MessageId = Guid.NewGuid(),
                        RequestedAtUtc = Now.AddMinutes(1)
                    },
                    (_, _) => throw new InvalidOperationException(
                        "A persisted Station job cancellation must not be replayed."));

                Assert.Equal(first, duplicate);
                Assert.Equal(request.MessageId, duplicate.RequestMessageId);
                Assert.Equal(request.JobId, duplicate.JobId);
                Assert.Equal(1, executionCount);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task JobCancellationRejectsSameIdempotencyKeyWithDifferentOperatorEvidence()
    {
        var path = DatabasePath();
        var request = JobCancel();
        var executionCount = 0;
        try
        {
            using var store = Store(path);
            var coordinator = new StationSafetyCommandCoordinator(store, new FixedClock(Now));
            _ = await coordinator.HandleJobCancelAsync(
                request,
                (_, _) =>
                {
                    executionCount++;
                    return ValueTask.FromResult(StationJobCancelExecutionResult.Success());
                });

            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await coordinator.HandleJobCancelAsync(
                    request with
                    {
                        MessageId = Guid.NewGuid(),
                        ActorId = "operator-002",
                        Reason = "Different cancellation evidence",
                        RequestedAtUtc = Now.AddSeconds(1)
                    },
                    (_, _) => throw new InvalidOperationException(
                        "A mismatched idempotency key must not reach execution.")));

            Assert.Contains("reused with different evidence", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, executionCount);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public async Task PendingJobCancellationAfterRestartRequiresRecoveryWithoutReplay()
    {
        var path = DatabasePath();
        var request = JobCancel();
        var executionCount = 0;
        try
        {
            using (var store = Store(path))
            {
                var coordinator = new StationSafetyCommandCoordinator(store, new FixedClock(Now));
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await coordinator.HandleJobCancelAsync(
                        request,
                        (_, _) =>
                        {
                            executionCount++;
                            throw new InvalidOperationException(
                                "Simulated Agent loss after cancellation dispatch.");
                        }));
            }

            using (var store = Store(path))
            {
                var restarted = new StationSafetyCommandCoordinator(
                    store,
                    new FixedClock(Now.AddMinutes(1)));
                var acknowledgement = await restarted.HandleJobCancelAsync(
                    request with
                    {
                        MessageId = Guid.NewGuid(),
                        RequestedAtUtc = Now.AddMinutes(1)
                    },
                    (_, _) => throw new InvalidOperationException(
                        "An uncertain cancellation must not be replayed."));

                Assert.False(acknowledgement.Accepted);
                Assert.Equal(
                    "Agent.JobCancellationRecoveryRequired",
                    acknowledgement.FailureCode);
                Assert.Contains("not replayed", acknowledgement.FailureReason, StringComparison.Ordinal);
                Assert.Equal(1, executionCount);
            }
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static EmergencyStopRequested EmergencyStop() => new(
        Guid.NewGuid(),
        "safety/emergency/station-assembly/0001",
        "agent-station-assembly",
        "station-assembly",
        "Emergency guard opened",
        "operator-001",
        Now);

    private static StationSafeStopRequested SafeStop() => new(
        Guid.NewGuid(),
        "safety/safe-stop/station-assembly/run-0001",
        "agent-station-assembly",
        "station-assembly",
        "system-station-assembly",
        Guid.NewGuid(),
        "operation-assemble@1",
        "operator-001",
        "Stop after the current safe boundary",
        Now);

    private static StationJobCancelRequested JobCancel() => new(
        Guid.NewGuid(),
        "run-0001/operation-assemble@1/cancel",
        Guid.NewGuid(),
        "run-0001/operation-assemble@1",
        "agent-station-assembly",
        "station-assembly",
        "system-station-assembly",
        Guid.NewGuid(),
        "operation-assemble@1",
        "operator-001",
        "Terminate the active vendor process tree",
        Now);

    private static string DatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"openlineops-agent-safety-{Guid.NewGuid():N}.db");

    private static SqliteStationSafetyInboxStore Store(string path) =>
        new($"Data Source={path}");

    private static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(path);
        File.Delete(path + "-shm");
        File.Delete(path + "-wal");
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
