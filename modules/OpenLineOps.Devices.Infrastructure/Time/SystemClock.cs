using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Devices.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
