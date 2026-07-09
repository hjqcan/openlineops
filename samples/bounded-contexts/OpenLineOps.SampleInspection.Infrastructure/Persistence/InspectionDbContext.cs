using Microsoft.EntityFrameworkCore;
using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Infrastructure.Data.Core.Context;
using OpenLineOps.SampleInspection.Domain.Plans;

namespace OpenLineOps.SampleInspection.Infrastructure.Persistence;

public sealed class InspectionDbContext(
    DbContextOptions<InspectionDbContext> options,
    IDomainEventDispatcher? domainEventDispatcher = null)
    : BaseDbContext(options, domainEventDispatcher)
{
    public DbSet<InspectionPlan> InspectionPlans => Set<InspectionPlan>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InspectionDbContext).Assembly);
    }
}
