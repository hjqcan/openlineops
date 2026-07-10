using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenLineOps.Infrastructure.Data.Core.Identifiers;
using OpenLineOps.SampleInspection.Domain.Identifiers;
using OpenLineOps.SampleInspection.Domain.Plans;
using OpenLineOps.SampleInspection.Infrastructure.Persistence;

namespace OpenLineOps.SampleInspection.Tests;

public sealed class InspectionPersistenceTemplateTests
{
    [Fact]
    public async Task EfDataCoreTemplatePersistsAggregateWithoutUnusedDomainEvents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = CreateOptions(connection);
        var planId = new InspectionPlanId("inspection.plan.visual-axis-check.v1");
        var createdAtUtc = new DateTimeOffset(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);
        var activatedAtUtc = new DateTimeOffset(2026, 6, 30, 8, 30, 0, TimeSpan.Zero);

        await using (var context = new InspectionDbContext(options))
        {
            await context.Database.MigrateAsync();

            var repository = new EfInspectionPlanRepository(context);
            var plan = InspectionPlan.Create(
                planId,
                "Visual axis check",
                "openlineops.motion-controller-x",
                createdAtUtc);

            var activateResult = plan.Activate(activatedAtUtc);

            repository.Add(plan);
            var committed = await repository.UnitOfWork.Commit();

            Assert.True(activateResult.Succeeded);
            Assert.True(committed);
            Assert.Empty(plan.DomainEvents);
        }

        await using (var context = new InspectionDbContext(options))
        {
            var repository = new EfInspectionPlanRepository(context);

            var restored = await repository.GetByIdAsync(planId);

            Assert.NotNull(restored);
            Assert.Equal("Visual axis check", restored.DisplayName);
            Assert.Equal("openlineops.motion-controller-x", restored.TargetDeviceId);
            Assert.Equal(InspectionPlanStatus.Active, restored.Status);
            Assert.Equal(activatedAtUtc, restored.ActivatedAtUtc);
            Assert.Empty(restored.DomainEvents);
        }

    }

    [Fact]
    public async Task EfModelUsesSharedStronglyTypedIdConverter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new InspectionDbContext(CreateOptions(connection));

        var entityType = context.Model.FindEntityType(typeof(InspectionPlan));
        var idProperty = entityType?.FindProperty(nameof(InspectionPlan.Id));

        Assert.NotNull(idProperty);
        Assert.IsType<StronglyTypedIdValueConverter<InspectionPlanId, string>>(
            idProperty.GetValueConverter());
    }

    [Fact]
    public async Task EfTemplateAppliesInitialMigration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new InspectionDbContext(CreateOptions(connection));

        await context.Database.MigrateAsync();
        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();

        Assert.Empty(pendingMigrations);
    }

    private static DbContextOptions<InspectionDbContext> CreateOptions(SqliteConnection connection)
    {
        return new DbContextOptionsBuilder<InspectionDbContext>()
            .UseSqlite(connection)
            .Options;
    }

}
