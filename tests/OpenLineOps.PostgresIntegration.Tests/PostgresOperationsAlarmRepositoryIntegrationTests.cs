using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    private const long OperationsSchemaLockId = 0x4F4C4F504F505343;

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
                ["OpenLineOps:Operations:Persistence:ConnectionString"] = _postgres.ConnectionString,
                ["OpenLineOps:EventBus:PublicationMode"] = "PostCommit"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddOpenLineOpsOperationsModule(configuration);
        services.AddOpenLineOpsEventBus(configuration);

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
    public async Task ConcurrentDbContextsInitializeEachDatabaseSchemaOnlyOncePerProcess()
    {
        const int concurrentRequestCount = 8;
        var suffix = Guid.NewGuid().ToString("N");
        var schema = $"operations_concurrency_{suffix}";
        var applicationName = $"openlineops-operations-concurrency-{suffix}";
        var connectionBuilder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = false,
            SearchPath = schema
        };
        await ExecuteAsync(
            _postgres.ConnectionString,
            $"CREATE SCHEMA \"{schema}\";");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenLineOps:Operations:Persistence:Provider"] =
                        OperationsPersistenceProviders.PostgreSql,
                    ["OpenLineOps:Operations:Persistence:ConnectionString"] =
                        connectionBuilder.ConnectionString
                })
                .Build();
            var services = new ServiceCollection();
            services.AddOpenLineOpsOperationsModule(configuration);
            using var serviceProvider = services.BuildServiceProvider();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, concurrentRequestCount)
                .Select(async index =>
                {
                    await start.Task.ConfigureAwait(false);
                    using var scope = serviceProvider.CreateScope();
                    var appService = scope.ServiceProvider.GetRequiredService<IAlarmAppService>();
                    return await appService.GetAsync($"operations.schema.concurrent.{index}");
                })
                .ToArray();

            var blockerBuilder = new NpgsqlConnectionStringBuilder(connectionBuilder.ConnectionString)
            {
                ApplicationName = $"{applicationName}-blocker"
            };
            await using var blocker = new NpgsqlConnection(blockerBuilder.ConnectionString);
            await blocker.OpenAsync().ConfigureAwait(false);
            await using (var blockerTransaction = await blocker
                             .BeginTransactionAsync()
                             .ConfigureAwait(false))
            {
                await AcquireOperationsSchemaLockAsync(blocker, blockerTransaction)
                    .ConfigureAwait(false);
                start.SetResult();

                await WaitForAdvisoryWaiterAsync(blocker.ProcessID).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                var waiterCount = await CountAdvisoryWaitersAsync(blocker.ProcessID)
                    .ConfigureAwait(false);
                await blockerTransaction.RollbackAsync().ConfigureAwait(false);

                Assert.Equal(1, waiterCount);
            }

            var results = await Task.WhenAll(requests)
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            Assert.All(results, Assert.Null);

            await using var verificationTransaction = await blocker
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            await AcquireOperationsSchemaLockAsync(blocker, verificationTransaction)
                .ConfigureAwait(false);
            var subsequentRequest = GetMissingAlarmFromNewScopeAsync(
                serviceProvider,
                "operations.schema.subsequent");
            var completedWithoutAdvisoryLock = await Task.WhenAny(
                    subsequentRequest,
                    Task.Delay(TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false) == subsequentRequest;
            await verificationTransaction.RollbackAsync().ConfigureAwait(false);
            Assert.Null(await subsequentRequest.ConfigureAwait(false));
            Assert.True(
                completedWithoutAdvisoryLock,
                "A new scoped DbContext repeated PostgreSQL schema initialization after readiness was established.");
        }
        finally
        {
            await ExecuteAsync(
                _postgres.ConnectionString,
                $"DROP SCHEMA \"{schema}\" CASCADE;");
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
                ["OpenLineOps:EventBus:PublicationMode"] = "Transactional",
                ["OpenLineOps:EventBus:RabbitMq:Enabled"] = "false"
            })
            .Build();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddOpenLineOpsOperationsModule(configuration);
                services.AddOpenLineOpsEventBus(configuration);
            })
            .Build();
        await host.StartAsync();
        using var scope = host.Services.CreateScope();

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
        await host.StopAsync();
    }

    [PostgresIntegrationFact]
    public async Task ProviderProfileRejectsHashIndexWithTheExpectedNameAndColumns()
    {
        await AssertIndexContractRejectedAsync(
            """
            DROP INDEX "IX_operations_alarms_RaisedAtUtc";
            CREATE INDEX "IX_operations_alarms_RaisedAtUtc"
                ON operations_alarms USING HASH ("RaisedAtUtc");
            """);
    }

    [PostgresIntegrationFact]
    public async Task ProviderProfileRejectsInvalidIndexWithTheExpectedNameAndColumns()
    {
        await AssertIndexContractRejectedAsync(
            """
            UPDATE pg_index AS index_metadata
            SET indisvalid = FALSE
            FROM pg_class AS index_relation
            INNER JOIN pg_namespace AS index_schema
                ON index_schema.oid = index_relation.relnamespace
            WHERE index_metadata.indexrelid = index_relation.oid
              AND index_schema.nspname = current_schema()
              AND index_relation.relname = 'IX_operations_alarms_RaisedAtUtc';
            """,
            expectedMutationCount: 1);
    }

    [PostgresIntegrationFact]
    public async Task ProviderProfileRejectsNotReadyIndexWithTheExpectedNameAndColumns()
    {
        await AssertIndexContractRejectedAsync(
            """
            UPDATE pg_index AS index_metadata
            SET indisready = FALSE
            FROM pg_class AS index_relation
            INNER JOIN pg_namespace AS index_schema
                ON index_schema.oid = index_relation.relnamespace
            WHERE index_metadata.indexrelid = index_relation.oid
              AND index_schema.nspname = current_schema()
              AND index_relation.relname = 'IX_operations_alarms_RaisedAtUtc';
            """,
            expectedMutationCount: 1);
    }

    private async Task AssertIndexContractRejectedAsync(
        string mutationSql,
        int? expectedMutationCount = null)
    {
        var schema = $"operations_contract_{Guid.NewGuid():N}";
        var connectionBuilder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            SearchPath = schema
        };
        await ExecuteAsync(
            _postgres.ConnectionString,
            $"CREATE SCHEMA \"{schema}\";");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenLineOps:Operations:Persistence:Provider"] =
                        OperationsPersistenceProviders.PostgreSql,
                    ["OpenLineOps:Operations:Persistence:ConnectionString"] =
                        connectionBuilder.ConnectionString
                })
                .Build();
            await CreateExpectedOperationsSchemaAsync(connectionBuilder.ConnectionString)
                .ConfigureAwait(false);

            var mutationCount = await ExecuteAsync(
                connectionBuilder.ConnectionString,
                mutationSql);
            if (expectedMutationCount is not null)
            {
                Assert.Equal(expectedMutationCount, mutationCount);
            }

            var restartedServices = new ServiceCollection();
            restartedServices.AddOpenLineOpsOperationsModule(configuration);
            using var restartedProvider = restartedServices.BuildServiceProvider();
            using var restartedScope = restartedProvider.CreateScope();
            var restartedAppService =
                restartedScope.ServiceProvider.GetRequiredService<IAlarmAppService>();

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => restartedAppService.GetAsync("operations.schema.validation"));
            Assert.Contains("index set", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(
                _postgres.ConnectionString,
                $"DROP SCHEMA \"{schema}\" CASCADE;");
        }
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

    private async Task WaitForAdvisoryWaiterAsync(int blockerProcessId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await CountAdvisoryWaitersAsync(blockerProcessId).ConfigureAwait(false) == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<long> CountAdvisoryWaitersAsync(int blockerProcessId)
    {
        return await CountAsync(
                _postgres.ConnectionString,
                """
                SELECT COUNT(1)
                FROM pg_locks AS waiting_lock
                WHERE waiting_lock.locktype = 'advisory'
                  AND NOT waiting_lock.granted
                  AND (
                      waiting_lock.database,
                      waiting_lock.classid,
                      waiting_lock.objid,
                      waiting_lock.objsubid)
                      IN (
                          SELECT held_lock.database,
                                 held_lock.classid,
                                 held_lock.objid,
                                 held_lock.objsubid
                          FROM pg_locks AS held_lock
                          WHERE held_lock.pid = @blocker_process_id
                            AND held_lock.locktype = 'advisory'
                            AND held_lock.granted);
                """,
                new Dictionary<string, object?>
                {
                    ["blocker_process_id"] = blockerProcessId
                })
            .ConfigureAwait(false);
    }

    private static async Task AcquireOperationsSchemaLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lock_id);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lock_id", OperationsSchemaLockId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<AlarmDetails?> GetMissingAlarmFromNewScopeAsync(
        IServiceProvider serviceProvider,
        string alarmId)
    {
        using var scope = serviceProvider.CreateScope();
        var appService = scope.ServiceProvider.GetRequiredService<IAlarmAppService>();
        return await appService.GetAsync(alarmId).ConfigureAwait(false);
    }

    private static async Task CreateExpectedOperationsSchemaAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var context = new OperationsDbContext(options);
        await ExecuteAsync(
                connectionString,
                context.Database.GenerateCreateScript())
            .ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
