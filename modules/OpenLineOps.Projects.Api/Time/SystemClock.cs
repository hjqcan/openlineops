using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Projects.Api.Time;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
