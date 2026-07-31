namespace OpenLineOps.Recipes.Domain.Assignments;

public sealed record RecipeAssignment
{
    public RecipeAssignment(
        Guid assignmentId,
        Recipes.RecipeRevisionSnapshot revisionSnapshot,
        string productModelId,
        string stationId,
        DateTimeOffset effectiveFromUtc,
        DateTimeOffset? effectiveUntilUtc,
        DateTimeOffset createdAtUtc,
        string createdBy,
        long revision = 1)
    {
        if (assignmentId == Guid.Empty)
        {
            throw new ArgumentException("Assignment id is required.", nameof(assignmentId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        if (revision != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "Recipe assignments are immutable revision-one facts.");
        }

        ArgumentNullException.ThrowIfNull(revisionSnapshot);
        AssignmentId = assignmentId;
        RevisionSnapshot = revisionSnapshot;
        RecipeId = revisionSnapshot.RecipeId;
        VersionId = revisionSnapshot.VersionId;
        ProductModelId = RecipeValueGuard.Required(productModelId, nameof(productModelId));
        StationId = RecipeValueGuard.Required(stationId, nameof(stationId));
        EffectiveFromUtc = RecipeValueGuard.Utc(effectiveFromUtc, nameof(effectiveFromUtc));
        EffectiveUntilUtc = effectiveUntilUtc is null
            ? null
            : RecipeValueGuard.Utc(effectiveUntilUtc.Value, nameof(effectiveUntilUtc));
        if (EffectiveUntilUtc is not null && EffectiveUntilUtc <= EffectiveFromUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveUntilUtc),
                "Assignment end must be after its start.");
        }

        ConfigurationSha256 = revisionSnapshot.ConfigurationSha256;
        CreatedAtUtc = RecipeValueGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        CreatedBy = RecipeValueGuard.Required(createdBy, nameof(createdBy));
        Revision = revision;
    }

    public Guid AssignmentId { get; }

    public Recipes.RecipeRevisionSnapshot RevisionSnapshot { get; }

    public string RecipeId { get; }

    public string VersionId { get; }

    public string ProductModelId { get; }

    public string StationId { get; }

    public DateTimeOffset EffectiveFromUtc { get; }

    public DateTimeOffset? EffectiveUntilUtc { get; }

    public string ConfigurationSha256 { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string CreatedBy { get; }

    public long Revision { get; }

    public bool IsEffectiveAt(DateTimeOffset utcNow)
    {
        var canonicalNow = RecipeValueGuard.Utc(utcNow, nameof(utcNow));
        return canonicalNow >= EffectiveFromUtc
            && (EffectiveUntilUtc is null || canonicalNow < EffectiveUntilUtc);
    }

    public bool Overlaps(RecipeAssignment other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(StationId, other.StationId, StringComparison.Ordinal)
            && string.Equals(ProductModelId, other.ProductModelId, StringComparison.Ordinal)
            && EffectiveFromUtc < (other.EffectiveUntilUtc ?? DateTimeOffset.MaxValue)
            && other.EffectiveFromUtc < (EffectiveUntilUtc ?? DateTimeOffset.MaxValue);
    }
}
