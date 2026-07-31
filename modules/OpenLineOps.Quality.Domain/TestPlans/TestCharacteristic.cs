using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Quality.Domain.Identifiers;

namespace OpenLineOps.Quality.Domain.TestPlans;

public sealed class TestCharacteristic : Entity<TestCharacteristicId>
{
    private TestCharacteristic(
        TestCharacteristicId id,
        string code,
        string displayName,
        string unit,
        string stepVersion,
        bool isRequired,
        LimitSet? limits)
        : base(id)
    {
        Code = QualityGuard.CanonicalText(code, nameof(code));
        DisplayName = QualityGuard.DisplayText(displayName, nameof(displayName));
        Unit = QualityGuard.CanonicalText(unit, nameof(unit));
        StepVersion = QualityGuard.CanonicalText(stepVersion, nameof(stepVersion));
        IsRequired = isRequired;

        if (limits is not null
            && !string.Equals(Unit, limits.Unit, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Characteristic unit '{Unit}' must exactly match limit unit '{limits.Unit}'.",
                nameof(limits));
        }

        if (isRequired && limits is null)
        {
            throw new ArgumentException(
                "Required characteristics must define acceptance limits.",
                nameof(limits));
        }

        Limits = limits;
    }

    public string Code { get; }

    public string DisplayName { get; }

    public string Unit { get; }

    public string StepVersion { get; }

    public bool IsRequired { get; }

    public LimitSet? Limits { get; }

    public static TestCharacteristic Create(
        TestCharacteristicId id,
        string code,
        string displayName,
        string unit,
        string stepVersion,
        bool isRequired,
        LimitSet? limits)
    {
        return new TestCharacteristic(
            id,
            code,
            displayName,
            unit,
            stepVersion,
            isRequired,
            limits);
    }
}
