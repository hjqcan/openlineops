using Microsoft.Data.Sqlite;
using OpenLineOps.Operations.Metrics.Application.Contracts;
using OpenLineOps.Operations.Metrics.Domain.Production;

namespace OpenLineOps.Operations.Metrics.Tests;

public sealed class SqliteOperationsMetricsStoreTests
{
    [Fact]
    public async Task ProductionEventsAreIdempotentOrderedAndSurviveColdRestart()
    {
        using var fixture = OperationsMetricsTestData.CreateStoreFixture();
        var store = fixture.CreateStore();
        var firstReceived = OperationsMetricsTestData.BaseUtc.AddHours(12);
        var service = fixture.CreateService(firstReceived, store);
        var later = Command(
            "event-later",
            OperationsMetricsTestData.BaseUtc.AddHours(10));
        var earlier = Command(
            "event-earlier",
            OperationsMetricsTestData.BaseUtc.AddHours(9));

        var first = await service.RecordProductionEventAsync(later);
        await service.RecordProductionEventAsync(earlier);
        var retryService = fixture.CreateService(firstReceived.AddMinutes(5), store);
        var replay = await retryService.RecordProductionEventAsync(later);

        Assert.Equal(OperationsMetricsWriteOutcome.Applied, first.Outcome);
        Assert.Equal(OperationsMetricsWriteOutcome.Replayed, replay.Outcome);
        Assert.Equal(firstReceived, replay.Resource.ReceivedAtUtc);
        var changed = later with { Good = false };
        await Assert.ThrowsAsync<OperationsMetricsConflictException>(
            async () => await retryService.RecordProductionEventAsync(changed));

        var ordered = await store.QueryProductionEventsAsync(
            "station-a",
            OperationsMetricsTestData.BaseUtc.AddHours(8),
            OperationsMetricsTestData.BaseUtc.AddHours(11));
        Assert.Equal(
            ["event-earlier", "event-later"],
            ordered.Select(static item => item.EventId));

        store.Dispose();
        var restarted = fixture.CreateStore();
        var restored = await restarted.GetProductionEventAsync("event-later");
        Assert.NotNull(restored);
        Assert.Equal(firstReceived, restored.ReceivedAtUtc);
    }

    [Fact]
    public async Task FactsRejectUpdateDeleteAndHashTampering()
    {
        using var fixture = OperationsMetricsTestData.CreateStoreFixture();
        var store = fixture.CreateStore();
        await store.AppendProductionEventAsync(
            OperationsMetricsTestData.Completed(
                "event-immutable",
                OperationsMetricsTestData.BaseUtc.AddHours(9)));

        await using (var connection = new SqliteConnection(
                         fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var durability = connection.CreateCommand())
            {
                durability.CommandText = "PRAGMA journal_mode;";
                Assert.Equal(
                    "wal",
                    (string?)await durability.ExecuteScalarAsync());
                durability.CommandText = "PRAGMA synchronous;";
                Assert.Equal(
                    2L,
                    (long?)await durability.ExecuteScalarAsync());
            }

            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE operations_metric_production_events
                SET payload_json = '{}'
                WHERE event_id = 'event-immutable';
                """;
            await Assert.ThrowsAsync<SqliteException>(
                async () => await update.ExecuteNonQueryAsync());

            await using var delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM operations_metric_production_events
                WHERE event_id = 'event-immutable';
                """;
            await Assert.ThrowsAsync<SqliteException>(
                async () => await delete.ExecuteNonQueryAsync());

            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = """
                DROP TRIGGER operations_metric_events_no_update;
                UPDATE operations_metric_production_events
                SET payload_json = '{}'
                WHERE event_id = 'event-immutable';
                """;
            await corrupt.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await store.GetProductionEventAsync("event-immutable"));
    }

    private static RecordProductionEventCommand Command(
        string eventId,
        DateTimeOffset occurredAtUtc) =>
        new(
            eventId,
            "station-a",
            $"unit-{eventId}",
            ProductionEventKind.UnitCompleted,
            occurredAtUtc.AddMilliseconds(-10),
            occurredAtUtc,
            FirstAttempt: true,
            Good: true,
            TimeSpan.FromSeconds(40),
            CanonicalProductionEvent.CurrentSchemaVersion);
}
