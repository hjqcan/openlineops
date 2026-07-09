using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenLineOps.Operations.Infra.Data.Persistence;

public sealed class OperationsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<OperationsDbContext>
{
    public OperationsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlite("Data Source=openlineops-operations-design-time.sqlite")
            .Options;

        return new OperationsDbContext(options);
    }
}
