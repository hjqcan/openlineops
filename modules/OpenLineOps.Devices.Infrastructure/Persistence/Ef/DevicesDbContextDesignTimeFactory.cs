using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenLineOps.Devices.Infrastructure.Persistence.Ef;

public sealed class DevicesDbContextDesignTimeFactory : IDesignTimeDbContextFactory<DevicesDbContext>
{
    public DevicesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DevicesDbContext>()
            .UseSqlite("Data Source=openlineops-devices-design-time.sqlite")
            .Options;

        return new DevicesDbContext(options);
    }
}
