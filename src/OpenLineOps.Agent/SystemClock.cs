using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
