using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenLineOps.SampleInspection.Infrastructure.Persistence;

public sealed class InspectionDbContextDesignTimeFactory : IDesignTimeDbContextFactory<InspectionDbContext>
{
    public InspectionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseSqlite("Data Source=openlineops-sample-inspection-design-time.sqlite")
            .Options;

        return new InspectionDbContext(options);
    }
}
