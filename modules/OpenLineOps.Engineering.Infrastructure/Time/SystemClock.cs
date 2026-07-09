using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Engineering.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
