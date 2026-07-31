using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Recipes.Infrastructure.Time;

public sealed class RecipesSystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
