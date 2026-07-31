using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Maintenance.Infrastructure.Time;

public sealed class MaintenanceSystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
