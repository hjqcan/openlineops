using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Quality.Infrastructure.Time;

public sealed class QualitySystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
