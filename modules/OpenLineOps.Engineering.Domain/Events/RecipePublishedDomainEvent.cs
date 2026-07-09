using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Engineering.Domain.Identifiers;

namespace OpenLineOps.Engineering.Domain.Events;

public sealed record RecipePublishedDomainEvent(
    RecipeId RecipeId,
    RecipeVersionId VersionId,
    DateTimeOffset PublishedAtUtc)
    : DomainEvent("Engineering.RecipePublished");
