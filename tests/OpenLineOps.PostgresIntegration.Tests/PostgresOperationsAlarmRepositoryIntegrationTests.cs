using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenLineOps.EventBus.DependencyInjection;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Contract.Services;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Operations.Infra.CrossCutting.IoC.DependencyInjection;
using OpenLineOps.Operations.Infra.Data.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresOperationsAlarmRepositoryIntegrationTests
{
    private readonly PostgresContainerFixture _postgres;

    public PostgresOperationsAlarmRepositoryIntegrationTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [PostgresIntegrationFact]
    public async Task ProviderProfilePersistsAlarmLifecycleThroughOperationsAppService()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var alarmId = $"operations.alarm.postgres.{suffix}";
        var stationId = $"station-postgres-{suffix}";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenLineOps:Operations:Persistence:Provider"] = OperationsPersistenceProviders.PostgreSql,
                ["OpenLineOps:Operations:Persistence:ConnectionString"] = _postgres.ConnectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsOperationsModule(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        using (var scope = serviceProvider.CreateScope())
        {
            var appService = scope.ServiceProvider.GetRequiredService<IAlarmAppService>();
            var raised = await appService.RaiseAsync(new RaiseAlarmRequest(
                alarmId,
                stationId,
                "runtime",
                $"session-{suffix}",
                AlarmSeverity.Critical,
                "Runtime command failed",
                "The command failed during PostgreSQL integration verification.",
                new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero)));

            Assert.Equal(alarmId, raised.Id);
            Assert.Equal(stationId, raised.StationId);
        }

        using (var restartedScope = serviceProvider.CreateScope())
        {
            var appService = restartedScope.ServiceProvider.GetRequiredService<IAlarmAppService>();
            var restored = await appService.GetAsync(alarmId);
            var openAlarms = await appService.GetOpenByStationAsync(stationId);

            Assert.NotNull(restored);
            Assert.Equal(AlarmSeverity.Critical, restored.Severity);
            var openAlarm = Assert.Single(openAlarms);
            Assert.Equal(alarmId, openAlarm.Id);
        }
    }

    [PostgresIntegrationFact]
    public async Task TransactionCoordinatorCommitsAlarmAndCapOutboxRecordTogether()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var alarmId = $"operations.alarm.cap-tx.{suffix}";
        var stationId = $"station-cap-tx-{suffix}";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OpenLineOpsEventBus"] = _postgres.ConnectionString,
                ["OpenLineOps:Operations:Persistence:Provider"] = OperationsPersistenceProviders.PostgreSql,
                ["OpenLineOps:Operations:Persistence:ConnectionString"] = _postgres.ConnectionString,
                ["OpenLineOps:EventBus:UseInMemory"] = "false",
                ["OpenLineOps:EventBus:ConnectionStringName"] = "OpenLineOpsEventBus",
                ["OpenLineOps:EventBus:PostgreSqlSchema"] = "cap",
                ["OpenLineOps:EventBus:EnableEfCoreTransactionCoordinator"] = "true",
                ["OpenLineOps:EventBus:RabbitMq:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsOperationsModule(configuration);
        services.AddOpenLineOpsEventBus(configuration);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IIntegrationEventTransactionCoordinator>());

        var appService = scope.ServiceProvider.GetRequiredService<IAlarmAppService>();

        await appService.RaiseAsync(new RaiseAlarmRequest(
            alarmId,
            stationId,
            "runtime",
            $"session-{suffix}",
            AlarmSeverity.Critical,
            "Runtime command failed",
            "The command failed during CAP transaction verification.",
            new DateTimeOffset(2026, 6, 30, 11, 0, 0, TimeSpan.Zero)));

        Assert.Equal(
            1,
            await CountAsync(
                _postgres.ConnectionString,
                """SELECT COUNT(1) FROM operations_alarms WHERE "Id" = @alarm_id;""",
                new Dictionary<string, object?> { ["alarm_id"] = alarmId }));
        Assert.True(
            await CountAsync(
                _postgres.ConnectionString,
                """SELECT COUNT(1) FROM cap.published;""") >= 1);
    }

    private static async Task<long> CountAsync(
        string connectionString,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters ?? new Dictionary<string, object?>())
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
