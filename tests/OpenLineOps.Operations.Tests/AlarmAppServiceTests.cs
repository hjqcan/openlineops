using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Domain.Abstractions.EventBus;
using OpenLineOps.Infrastructure.Data.Core.EventBus;
using OpenLineOps.Operations.Application.Contract.Alarms;
using OpenLineOps.Operations.Application.Services;
using OpenLineOps.Operations.Domain.Shared.Enums;
using OpenLineOps.Operations.Infra.Data.Persistence;

namespace OpenLineOps.Operations.Tests;

public sealed class AlarmAppServiceTests
{
    [Fact]
    public async Task RaiseAcknowledgeAndSourceClearPersistThroughApplicationContract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlite(connection)
            .Options;

        AlarmDetails details;
        await using (var context = new OperationsDbContext(
                         options,
                         new IntegrationEventPublicationPolicy(IntegrationEventPublicationMode.PostCommit),
                         integrationEventPublisher: new CapturingIntegrationEventPublisher()))
        {
            await context.Database.MigrateAsync();

            var repository = new EfAlarmRepository(context);
            var appService = new AlarmAppService(repository);

            details = await appService.RaiseAsync(new RaiseAlarmRequest(
                "operations.alarm.application",
                "station-beta",
                "runtime",
                "session-beta",
                AlarmSeverity.Major,
                "Runtime failed",
                "Runtime command failed."));
        }

        Assert.Equal("operations.alarm.application", details.Id);
        Assert.Equal("station-beta", details.StationId);
        Assert.Equal(AlarmStatus.Raised, details.Status);

        await using (var context = new OperationsDbContext(options))
        {
            var repository = new EfAlarmRepository(context);
            var appService = new AlarmAppService(repository);

            var rejectedClearance = await appService.ClearSourceAsync(
                details.Id,
                new ClearAlarmSourceRequest(
                    "agent-other",
                    "station-other",
                    "Unrelated station reported recovery."));

            Assert.False(rejectedClearance.Succeeded);
            Assert.Equal("Operations.Alarm.StationMismatch", rejectedClearance.Code);
        }

        await using (var context = new OperationsDbContext(options))
        {
            var repository = new EfAlarmRepository(context);
            var appService = new AlarmAppService(repository);

            var acknowledgement = await appService.AcknowledgeAsync(
                details.Id,
                new AcknowledgeAlarmRequest("operator-b"));

            Assert.True(acknowledgement.Succeeded);
        }

        await using (var context = new OperationsDbContext(options))
        {
            var repository = new EfAlarmRepository(context);
            var appService = new AlarmAppService(repository);

            var resolution = await appService.ClearSourceAsync(
                details.Id,
                new ClearAlarmSourceRequest(
                    "agent-b",
                    "station-beta",
                    "Device recovered."));

            Assert.True(resolution.Succeeded);
        }

        AlarmDetails? restored;
        await using (var context = new OperationsDbContext(options))
        {
            var repository = new EfAlarmRepository(context);
            var appService = new AlarmAppService(repository);

            restored = await appService.GetAsync(details.Id);
        }

        Assert.NotNull(restored);
        Assert.Equal(AlarmStatus.Resolved, restored.Status);
        Assert.Equal("operator-b", restored.AcknowledgedBy);
        Assert.Equal("agent-b", restored.ResolvedBy);
        Assert.Equal("Device recovered.", restored.ResolutionNote);
    }

    private sealed class CapturingIntegrationEventPublisher : IIntegrationEventPublisher
    {
        public Task PublishAsync(
            IEnumerable<object> domainEvents,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
