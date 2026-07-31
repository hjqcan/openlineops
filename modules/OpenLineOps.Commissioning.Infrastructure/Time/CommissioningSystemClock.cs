using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Commissioning.Infrastructure.Time;

public sealed class CommissioningSystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
