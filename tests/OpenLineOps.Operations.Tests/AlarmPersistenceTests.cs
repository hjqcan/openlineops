using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Operations.Domain.Aggregates;
using OpenLineOps.Operations.Domain.Events;
using OpenLineOps.Operations.Domain.Events.Converters;
using OpenLineOps.Operations.Domain.Identifiers;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Operations.Infra.Data.Persistence;

namespace OpenLineOps.Operations.Tests;

public sealed class AlarmPersistenceTests
{
    [Fact]
    public async Task EfDataCoreRepositoryPersistsAlarmLifecycleAndDispatchesDomainEvents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var dispatcher = new CapturingDomainEventDispatcher();
        var publisher = new CapturingIntegrationEventPublisher();
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlite(connection)
            .Options;
        var alarm = Alarm.Raise(
            new AlarmId("operations.alarm.persistence.v1"),
            "station-alpha",
            "runtime",
            "session-001",
            AlarmSeverity.Critical,
            "Command failed",
            "The runtime command failed.",
            new DateTimeOffset(2026, 6, 30, 8, 0, 0, TimeSpan.Zero));

        await using (var context = new OperationsDbContext(options, dispatcher, publisher))
        {
            await context.Database.MigrateAsync();

            var repository = new EfAlarmRepository(context);
            repository.Add(alarm);

            var committed = await repository.UnitOfWork.Commit();

            Assert.True(committed);
            Assert.Empty(alarm.DomainEvents);
        }

        await using (var context = new OperationsDbContext(options))
        {
            var repository = new EfAlarmRepository(context);
            var restored = await repository.GetByIdAsync(alarm.Id);

            Assert.NotNull(restored);
            Assert.Equal(alarm.Id, restored.Id);
            Assert.Equal("station-alpha", restored.StationId);
            Assert.Equal(AlarmSeverity.Critical, restored.Severity);
            Assert.Equal(AlarmStatus.Raised, restored.Status);
            Assert.Empty(restored.DomainEvents);

            var openAlarms = await repository.GetOpenByStationAsync("station-alpha");
            Assert.Single(openAlarms);
        }

        var raisedEvent = Assert.IsType<AlarmRaisedDomainEvent>(
            Assert.Single(dispatcher.Dispatched));
        Assert.Same(raisedEvent, Assert.Single(publisher.Published));

        var integrationDto = raisedEvent.ToIntegrationDto();

        Assert.Equal(alarm.Id.Value, integrationDto.AlarmId);
        Assert.Equal("station-alpha", integrationDto.StationId);
        Assert.Equal(AlarmSeverity.Critical, integrationDto.Severity);
    }

    [Fact]
    public async Task GetOpenByStationExcludesResolvedAlarms()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new OperationsDbContext(options);
        await context.Database.MigrateAsync();

        var open = Alarm.Raise(
            new AlarmId("operations.alarm.open.v1"),
            "station-alpha",
            "device",
            "device-001",
            AlarmSeverity.Warning,
            "Door open",
            "Fixture door is open.",
            DateTimeOffset.UtcNow);
        var resolved = Alarm.Raise(
            new AlarmId("operations.alarm.resolved.v1"),
            "station-alpha",
            "device",
            "device-002",
            AlarmSeverity.Major,
            "Pressure low",
            "Air pressure is below threshold.",
            DateTimeOffset.UtcNow);
        resolved.Resolve("operator-a", "Pressure restored.", DateTimeOffset.UtcNow);

        var repository = new EfAlarmRepository(context);
        repository.Add(open);
        repository.Add(resolved);
        await repository.UnitOfWork.Commit();

        var openAlarms = await repository.GetOpenByStationAsync("station-alpha");

        Assert.Single(openAlarms);
        Assert.Equal(open.Id, openAlarms.Single().Id);
    }

    [Fact]
    public async Task OperationsDbContextAppliesInitialMigration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new OperationsDbContext(options);

        await context.Database.MigrateAsync();
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();

        Assert.Empty(pendingMigrations);
    }

    [Fact]
    public async Task EfAlarmRepositoryAppliesMigrationsBeforeFirstWrite()
    {
        using var database = TemporarySqliteDatabase.Create();
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlite(database.ConnectionString)
            .Options;
        var alarmId = new AlarmId($"operations.alarm.auto-migrate.{Guid.NewGuid():N}");

        await using (var context = new OperationsDbContext(options))
        {
            var repository = new EfAlarmRepository(context);
            repository.Add(Alarm.Raise(
                alarmId,
                "station-auto-migrate",
                "runtime",
                "session-auto-migrate",
                AlarmSeverity.Warning,
                "Auto migration",
                "Repository should apply migrations before saving.",
                DateTimeOffset.UtcNow));

            var committed = await repository.UnitOfWork.Commit();

            Assert.True(committed);
        }

        await using (var context = new OperationsDbContext(options))
        {
            var repository = new EfAlarmRepository(context);
            var restored = await repository.GetByIdAsync(alarmId);

            Assert.NotNull(restored);
            Assert.Equal("station-auto-migrate", restored.StationId);
        }
    }

    private sealed class CapturingDomainEventDispatcher : IDomainEventDispatcher
    {
        public List<IDomainEvent> Dispatched { get; } = [];

        public Task DispatchAsync(
            IReadOnlyCollection<IDomainEvent> domainEvents,
            CancellationToken cancellationToken = default)
        {
            Dispatched.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingIntegrationEventPublisher : IIntegrationEventPublisher
    {
        public List<object> Published { get; } = [];

        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            Published.AddRange(domainEvents);
            return Task.CompletedTask;
        }
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        private string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "operations-ef.sqlite");

            return new TemporarySqliteDatabase(directory, databasePath);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
