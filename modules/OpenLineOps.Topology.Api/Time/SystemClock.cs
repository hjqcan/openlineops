using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Topology.Api.Time;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
