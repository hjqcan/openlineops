using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Services;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Repositories;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Operations.Infra.Data.Persistence;

namespace OpenLineOps.Operations.Tests;

public sealed class AlarmLifecycleHardeningTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task LatchingAlarmUsesBoundedTemporalStatesAndSourceOwnedClearance()
    {
        using var database = TemporarySqliteDatabase.Create();
        var clock = new ManualTimeProvider(Baseline);
        await using var context = CreateContext(database.ConnectionString);
        var service = new AlarmAppService(new EfAlarmRepository(context), clock);

        var definition = await service.RegisterDefinitionAsync(
            DefinitionRequest("definition-register-1"));
        Assert.True(definition.Succeeded);

        var raised = await service.RaiseCommandAsync(
            RaiseRequest("alarm-lifecycle-1", "alarm-raise-1"));
        Assert.True(raised.Succeeded);
        Assert.Equal(1, raised.Alarm!.Version);
        Assert.True(raised.Alarm.SourceActive);
        Assert.True(raised.Alarm.BuzzerActive);
        Assert.False(raised.Alarm.EscalationDue);

        var wrongStation = await service.ClearSourceAsync(
            raised.Alarm.Id,
            new ClearAlarmSourceRequest(
                "agent-other",
                "station-other",
                "Attempted unrelated clearance.",
                "alarm-clear-wrong-station",
                1));
        Assert.False(wrongStation.Succeeded);
        Assert.Equal("Operations.Alarm.StationMismatch", wrongStation.Code);

        var shelfRequest = new ShelfAlarmRequest(
            "operator-a",
            "Investigating fixture.",
            Baseline.AddMinutes(5),
            "alarm-shelf-1",
            1);
        var shelved = await service.ShelfAsync(raised.Alarm.Id, shelfRequest);
        Assert.True(shelved.Succeeded);
        Assert.Equal(2, shelved.Version);

        var shelfReplay = await service.ShelfAsync(raised.Alarm.Id, shelfRequest);
        Assert.True(shelfReplay.Succeeded);
        Assert.True(shelfReplay.Replayed);
        Assert.Equal(2, shelfReplay.Version);

        var changedReplay = await service.ShelfAsync(
            raised.Alarm.Id,
            shelfRequest with { Comment = "Changed command content." });
        Assert.False(changedReplay.Succeeded);
        Assert.Equal("Operations.Alarm.IdempotencyConflict", changedReplay.Code);

        var duringShelf = await service.GetAsync(raised.Alarm.Id);
        Assert.True(duringShelf!.IsShelved);
        Assert.False(duringShelf.BuzzerActive);

        clock.Advance(TimeSpan.FromMinutes(6));
        var afterShelf = await service.GetAsync(raised.Alarm.Id);
        Assert.False(afterShelf!.IsShelved);
        Assert.True(afterShelf.BuzzerActive);
        Assert.True(afterShelf.EscalationDue);
        Assert.Equal(AlarmPolicyAction.HoldNewDispatch, afterShelf.EscalationAction);

        var wrongSuppressionSource = await service.SuppressAsync(
            raised.Alarm.Id,
            new SuppressAlarmRequest(
                "engineer-a",
                "different-source",
                "Maintenance window.",
                clock.GetUtcNow().AddMinutes(30),
                "alarm-suppress-wrong-source",
                2));
        Assert.False(wrongSuppressionSource.Succeeded);
        Assert.Equal(
            "Operations.Alarm.SuppressionSourceMismatch",
            wrongSuppressionSource.Code);

        var suppressed = await service.SuppressAsync(
            raised.Alarm.Id,
            new SuppressAlarmRequest(
                "engineer-a",
                "plc",
                "Approved maintenance window.",
                clock.GetUtcNow().AddMinutes(30),
                "alarm-suppress-1",
                2));
        Assert.True(suppressed.Succeeded);
        Assert.Equal(3, suppressed.Version);

        var staleAcknowledgement = await service.AcknowledgeAsync(
            raised.Alarm.Id,
            new AcknowledgeAlarmRequest(
                "operator-a",
                "Reviewed.",
                "alarm-ack-stale",
                2));
        Assert.False(staleAcknowledgement.Succeeded);
        Assert.Equal("Operations.Alarm.VersionConflict", staleAcknowledgement.Code);

        var acknowledged = await service.AcknowledgeAsync(
            raised.Alarm.Id,
            new AcknowledgeAlarmRequest(
                "operator-a",
                "Reviewed and contained.",
                "alarm-ack-1",
                3));
        Assert.True(acknowledged.Succeeded);
        Assert.Equal(4, acknowledged.Version);

        var acknowledgedWhileSourceActive = await service.GetAsync(raised.Alarm.Id);
        Assert.Equal(AlarmStatus.Acknowledged, acknowledgedWhileSourceActive!.Status);
        Assert.True(acknowledgedWhileSourceActive.SourceActive);
        Assert.Null(acknowledgedWhileSourceActive.SourceClearedAtUtc);

        var cleared = await service.ClearSourceAsync(
            raised.Alarm.Id,
            new ClearAlarmSourceRequest(
                "station-agent-a",
                "station-alpha",
                "PLC source returned to normal.",
                "alarm-clear-1",
                4));
        Assert.True(cleared.Succeeded);
        Assert.Equal(5, cleared.Version);

        var final = await service.GetAsync(raised.Alarm.Id);
        Assert.Equal(AlarmStatus.Resolved, final!.Status);
        Assert.False(final.SourceActive);
        Assert.Equal("station-agent-a", final.SourceClearedBy);
        Assert.Equal("PLC source returned to normal.", final.SourceClearanceNote);
        Assert.Equal("Reviewed and contained.", final.AcknowledgementComment);
        Assert.False(final.BuzzerActive);

        var facts = await service.GetFactsAsync(raised.Alarm.Id);
        Assert.Equal(
            [
                AlarmLifecycleAction.Raised,
                AlarmLifecycleAction.Shelved,
                AlarmLifecycleAction.Suppressed,
                AlarmLifecycleAction.Acknowledged,
                AlarmLifecycleAction.SourceCleared
            ],
            facts.Select(fact => fact.Action));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], facts.Select(fact => fact.AlarmVersion));
    }

    [Fact]
    public async Task SourceClearDoesNotResolveUnacknowledgedLatchingAlarm()
    {
        using var database = TemporarySqliteDatabase.Create();
        var clock = new ManualTimeProvider(Baseline);
        await using var context = CreateContext(database.ConnectionString);
        var service = new AlarmAppService(new EfAlarmRepository(context), clock);
        await service.RegisterDefinitionAsync(DefinitionRequest("definition-register-2"));
        var raised = await service.RaiseCommandAsync(
            RaiseRequest("alarm-lifecycle-2", "alarm-raise-2"));

        var sourceClear = await service.ClearSourceAsync(
            raised.Alarm!.Id,
            new ClearAlarmSourceRequest(
                "station-agent-a",
                "station-alpha",
                "Source is normal.",
                "alarm-clear-2",
                1));
        Assert.True(sourceClear.Succeeded);

        var awaitingAcknowledgement = await service.GetAsync(raised.Alarm.Id);
        Assert.False(awaitingAcknowledgement!.SourceActive);
        Assert.Equal(AlarmStatus.Raised, awaitingAcknowledgement.Status);
        Assert.Equal("station-agent-a", awaitingAcknowledgement.SourceClearedBy);
        Assert.Null(awaitingAcknowledgement.ResolvedAtUtc);
        Assert.True(awaitingAcknowledgement.BuzzerActive);

        var acknowledgement = await service.AcknowledgeAsync(
            raised.Alarm.Id,
            new AcknowledgeAlarmRequest(
                "operator-b",
                "Reviewed after source recovery.",
                "alarm-ack-2",
                2));
        Assert.True(acknowledgement.Succeeded);

        var resolved = await service.GetAsync(raised.Alarm.Id);
        Assert.Equal(AlarmStatus.Resolved, resolved!.Status);
        Assert.False(resolved.BuzzerActive);
    }

    [Fact]
    public async Task DefinitionsAndFactsAreImmutableAndHashChainSurvivesColdRestart()
    {
        using var database = TemporarySqliteDatabase.Create();
        var clock = new ManualTimeProvider(Baseline);

        await using (var context = CreateContext(database.ConnectionString))
        {
            var service = new AlarmAppService(new EfAlarmRepository(context), clock);
            var created = await service.RegisterDefinitionAsync(
                DefinitionRequest("definition-register-3"));
            Assert.True(created.Succeeded);

            var exactReplay = await service.RegisterDefinitionAsync(
                DefinitionRequest("definition-register-3"));
            Assert.True(exactReplay.Succeeded);
            Assert.True(exactReplay.Replayed);

            var changedReplay = await service.RegisterDefinitionAsync(
                DefinitionRequest("definition-register-3") with
                {
                    Description = "Changed definition."
                });
            Assert.False(changedReplay.Succeeded);
            Assert.Equal("Operations.Alarm.IdempotencyConflict", changedReplay.Code);

            var raised = await service.RaiseCommandAsync(
                RaiseRequest("alarm-lifecycle-3", "alarm-raise-3"));
            Assert.True(raised.Succeeded);
        }

        await using (var context = CreateContext(database.ConnectionString))
        {
            var service = new AlarmAppService(new EfAlarmRepository(context), clock);
            var restored = await service.GetAsync("alarm-lifecycle-3");
            var facts = await service.GetFactsAsync("alarm-lifecycle-3");

            Assert.NotNull(restored);
            Assert.Single(facts);
            Assert.Equal(AlarmLifecycleAction.Raised, facts.Single().Action);
        }

        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var immutableDefinition = connection.CreateCommand();
            immutableDefinition.CommandText =
                "UPDATE operations_alarm_definitions SET \"Title\" = 'tampered';";
            await Assert.ThrowsAsync<SqliteException>(
                () => immutableDefinition.ExecuteNonQueryAsync());

            await using var immutableFact = connection.CreateCommand();
            immutableFact.CommandText =
                "UPDATE operations_alarm_lifecycle_facts SET \"PayloadJson\" = '{}';";
            await Assert.ThrowsAsync<SqliteException>(
                () => immutableFact.ExecuteNonQueryAsync());

            await using var bypass = connection.CreateCommand();
            bypass.CommandText = """
                DROP TRIGGER operations_alarm_lifecycle_facts_no_update;
                UPDATE operations_alarm_lifecycle_facts
                SET "PayloadJson" = '{"tampered":true}';
                """;
            await bypass.ExecuteNonQueryAsync();
        }

        await using (var context = CreateContext(database.ConnectionString))
        {
            var service = new AlarmAppService(new EfAlarmRepository(context), clock);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.GetAsync("alarm-lifecycle-3"));
        }
    }

    [Fact]
    public async Task OpenAlarmQueryUsesDeterministicFirstOutOrdering()
    {
        using var database = TemporarySqliteDatabase.Create();
        var clock = new ManualTimeProvider(Baseline);
        await using var context = CreateContext(database.ConnectionString);
        var service = new AlarmAppService(new EfAlarmRepository(context), clock);

        foreach (var alarm in new[]
                 {
                     ("alarm-late", Baseline.AddSeconds(20)),
                     ("alarm-tie-z", Baseline.AddSeconds(10)),
                     ("alarm-first", Baseline),
                     ("alarm-tie-a", Baseline.AddSeconds(10))
                 })
        {
            var result = await service.RaiseCommandAsync(new RaiseAlarmRequest(
                alarm.Item1,
                "station-first-out",
                "runtime",
                alarm.Item1,
                AlarmSeverity.Warning,
                "Runtime warning",
                "Runtime warning.",
                alarm.Item2,
                CommandId: $"raise:{alarm.Item1}"));
            Assert.True(result.Succeeded);
        }

        var ordered = await service.GetOpenByStationAsync("station-first-out");

        Assert.Equal(
            ["alarm-first", "alarm-tie-a", "alarm-tie-z", "alarm-late"],
            ordered.Select(alarm => alarm.Id));
    }

    [Fact]
    public async Task RepositoryCompareAndSwapRejectsStaleConcurrentProjection()
    {
        using var database = TemporarySqliteDatabase.Create();
        var clock = new ManualTimeProvider(Baseline);
        await using (var setupContext = CreateContext(database.ConnectionString))
        {
            var setupService = new AlarmAppService(
                new EfAlarmRepository(setupContext),
                clock);
            var raised = await setupService.RaiseCommandAsync(new RaiseAlarmRequest(
                "alarm-cas-1",
                "station-cas",
                "runtime",
                "runtime-cas",
                AlarmSeverity.Major,
                "Concurrent command",
                "Concurrent command verification.",
                Baseline,
                CommandId: "alarm-cas-raise"));
            Assert.True(raised.Succeeded);
        }

        await using var firstContext = CreateContext(database.ConnectionString);
        await using var staleContext = CreateContext(database.ConnectionString);
        var firstRepository = new EfAlarmRepository(firstContext);
        var staleRepository = new EfAlarmRepository(staleContext);
        var alarmId = new AlarmId("alarm-cas-1");
        var first = await firstRepository.GetByIdAsync(alarmId);
        var stale = await staleRepository.GetByIdAsync(alarmId);
        Assert.NotNull(first);
        Assert.NotNull(stale);
        var previousHash = await firstRepository.GetLastFactHashAsync(alarmId);
        var changedAtUtc = Baseline.AddSeconds(1);

        Assert.True(first.Acknowledge(
            "operator-first",
            "First writer.",
            changedAtUtc).Succeeded);
        firstRepository.UpdateWithExpectedVersion(first, expectedVersion: 1);
        firstRepository.AddFact(AlarmLifecycleFact.Create(
            "operations.alarm.fact.cas.first",
            alarmId.Value,
            first.Version,
            "alarm-cas-first",
            new string('a', 64),
            AlarmLifecycleAction.Acknowledged,
            "operator-first",
            changedAtUtc,
            """{"comment":"First writer."}""",
            previousHash));

        Assert.True(stale.Acknowledge(
            "operator-stale",
            "Stale writer.",
            changedAtUtc).Succeeded);
        staleRepository.UpdateWithExpectedVersion(stale, expectedVersion: 1);

        Assert.Equal(
            AlarmCommitOutcome.Committed,
            await firstRepository.CommitAlarmChangesAsync());
        Assert.Equal(
            AlarmCommitOutcome.ConcurrencyConflict,
            await staleRepository.CommitAlarmChangesAsync());
    }

    private static OperationsDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new OperationsDbContext(
            options,
            new IntegrationEventPublicationPolicy(IntegrationEventPublicationMode.PostCommit),
            integrationEventPublisher: new NoOpIntegrationEventPublisher());
    }

    private static RegisterAlarmDefinitionRequest DefinitionRequest(string commandId) =>
        new(
            "alarm-definition.plc.fault",
            "station-alpha",
            "plc",
            AlarmSeverity.Critical,
            "PLC fault",
            "The PLC reports an active fault.",
            IsLatching: true,
            RequiresBuzzer: true,
            MaximumShelfSeconds: 600,
            EscalationDelaySeconds: 60,
            EscalationAction: AlarmPolicyAction.HoldNewDispatch,
            CreatedBy: "engineer-a",
            CommandId: commandId);

    private static RaiseAlarmRequest RaiseRequest(string alarmId, string commandId) =>
        new(
            alarmId,
            "station-alpha",
            "plc",
            "plc-main",
            AlarmSeverity.Critical,
            "PLC fault",
            "The PLC reports an active fault.",
            Baseline,
            DefinitionId: "alarm-definition.plc.fault",
            CommandId: commandId,
            ActorId: "station-agent-a");

    private sealed class NoOpIntegrationEventPublisher : IIntegrationEventPublisher
    {
        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            DirectoryPath = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        private string DirectoryPath { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "OpenLineOps",
                "alarm-lifecycle",
                Guid.NewGuid().ToString("N"));
            return new TemporarySqliteDatabase(
                directory,
                Path.Combine(directory, "operations.sqlite"));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
