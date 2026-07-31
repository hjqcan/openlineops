using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Quality.Domain.Identifiers;

namespace OpenLineOps.Quality.Domain.TestPlans;

public sealed class LimitSet : Entity<LimitSetId>
{
    private LimitSet(
        LimitSetId id,
        string unit,
        decimal? lowerLimit,
        bool lowerInclusive,
        decimal? upperLimit,
        bool upperInclusive)
        : base(id)
    {
        if (lowerLimit is null && upperLimit is null)
        {
            throw new ArgumentException("At least one limit must be provided.");
        }

        if (lowerLimit > upperLimit)
        {
            throw new ArgumentException("Lower limit cannot exceed upper limit.");
        }

        if (lowerLimit == upperLimit
            && lowerLimit is not null
            && (!lowerInclusive || !upperInclusive))
        {
            throw new ArgumentException("Equal limits must both be inclusive.");
        }

        Unit = QualityGuard.CanonicalText(unit, nameof(unit));
        LowerLimit = lowerLimit;
        LowerInclusive = lowerInclusive;
        UpperLimit = upperLimit;
        UpperInclusive = upperInclusive;
    }

    public string Unit { get; }

    public decimal? LowerLimit { get; }

    public bool LowerInclusive { get; }

    public decimal? UpperLimit { get; }

    public bool UpperInclusive { get; }

    public static LimitSet Create(
        LimitSetId id,
        string unit,
        decimal? lowerLimit,
        bool lowerInclusive,
        decimal? upperLimit,
        bool upperInclusive)
    {
        return new LimitSet(id, unit, lowerLimit, lowerInclusive, upperLimit, upperInclusive);
    }

    public bool Contains(decimal value)
    {
        var satisfiesLower = LowerLimit is null
            || (LowerInclusive ? value >= LowerLimit : value > LowerLimit);
        var satisfiesUpper = UpperLimit is null
            || (UpperInclusive ? value <= UpperLimit : value < UpperLimit);

        return satisfiesLower && satisfiesUpper;
    }
}
