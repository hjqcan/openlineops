using OpenLineOps.Quality.Domain.Identifiers;
using OpenLineOps.Quality.Domain.TestPlans;

namespace OpenLineOps.Quality.Tests;

public sealed class TestPlanRevisionTests
{
    [Fact]
    public void StrongIdentifiersRejectEmptyValuesAndRemainStable()
    {
        Assert.Throws<ArgumentException>(() => new TestPlanId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TestPlanRevisionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TestCharacteristicId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new LimitSetId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TestAttemptId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new MeasurementResultId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new NonconformanceId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new CalibrationAssetId(Guid.Empty));

        var value = Guid.NewGuid();
        var id = new TestPlanId(value);

        Assert.Equal(value, id.Value);
        Assert.Equal(value.ToString("D"), id.ToString());
    }

    [Fact]
    public void LimitSetEvaluatesInclusiveAndExclusiveBoundaries()
    {
        var inclusive = QualityTestData.CreateLimits();
        var exclusive = QualityTestData.CreateLimits(
            lowerInclusive: false,
            upperInclusive: false);

        Assert.True(inclusive.Contains(4.5m));
        Assert.True(inclusive.Contains(5.5m));
        Assert.False(inclusive.Contains(4.49m));
        Assert.False(exclusive.Contains(4.5m));
        Assert.True(exclusive.Contains(5.0m));
        Assert.False(exclusive.Contains(5.5m));
    }

    [Fact]
    public void LimitSetRejectsMissingReversedOrImpossibleLimits()
    {
        Assert.Throws<ArgumentException>(
            () => QualityTestData.CreateLimits(lowerLimit: null, upperLimit: null));
        Assert.Throws<ArgumentException>(
            () => QualityTestData.CreateLimits(lowerLimit: 6m, upperLimit: 5m));
        Assert.Throws<ArgumentException>(
            () => QualityTestData.CreateLimits(
                lowerLimit: 5m,
                upperLimit: 5m,
                lowerInclusive: false));
    }

    [Fact]
    public void CharacteristicRequiresMatchingUnitsAndLimitsForRequiredChecks()
    {
        var ampereLimits = QualityTestData.CreateLimits(unit: "A");

        Assert.Throws<ArgumentException>(
            () => TestCharacteristic.Create(
                TestCharacteristicId.New(),
                "supply.voltage",
                "Supply voltage",
                "V",
                "step@1",
                isRequired: true,
                ampereLimits));
        Assert.Throws<ArgumentException>(
            () => TestCharacteristic.Create(
                TestCharacteristicId.New(),
                "supply.voltage",
                "Supply voltage",
                "V",
                "step@1",
                isRequired: true,
                limits: null));
    }

    [Fact]
    public void RevisionFreezesItsCharacteristicCollection()
    {
        var required = QualityTestData.CreateCharacteristic();
        var source = new List<TestCharacteristic> { required };
        var revision = TestPlanRevision.Create(
            TestPlanRevisionId.New(),
            TestPlanId.New(),
            1,
            "Functional test",
            source,
            QualityTestData.BaseTimeUtc);

        source.Add(QualityTestData.CreateCharacteristic(
            code: "supply.current",
            displayName: "Supply current",
            isRequired: false,
            unit: "A"));

        Assert.Single(revision.Characteristics);
        Assert.Equal(required, revision.GetCharacteristic(required.Id));
        Assert.Throws<NotSupportedException>(
            () => ((ICollection<TestCharacteristic>)revision.Characteristics)
                .Add(QualityTestData.CreateCharacteristic()));
    }

    [Fact]
    public void RevisionRejectsDuplicateIdentifiersCodesAndPlansWithoutRequiredChecks()
    {
        var first = QualityTestData.CreateCharacteristic();
        var duplicateId = TestCharacteristic.Create(
            first.Id,
            "supply.current",
            "Supply current",
            "A",
            "step-current@1",
            isRequired: true,
            QualityTestData.CreateLimits(unit: "A"));
        var duplicateCode = TestCharacteristic.Create(
            TestCharacteristicId.New(),
            first.Code,
            "Another voltage",
            "V",
            "step-voltage@2",
            isRequired: true,
            QualityTestData.CreateLimits());
        var informational = QualityTestData.CreateCharacteristic(
            code: "ambient.temperature",
            displayName: "Ambient temperature",
            isRequired: false,
            unit: "Cel");

        Assert.Throws<ArgumentException>(() => QualityTestData.CreatePlan(first, duplicateId));
        Assert.Throws<ArgumentException>(() => QualityTestData.CreatePlan(first, duplicateCode));
        Assert.Throws<ArgumentException>(() => QualityTestData.CreatePlan(informational));
    }

    [Fact]
    public void RevisionRejectsNonUtcCreationTimeAndUnknownCharacteristics()
    {
        var characteristic = QualityTestData.CreateCharacteristic();

        Assert.Throws<ArgumentException>(
            () => TestPlanRevision.Create(
                TestPlanRevisionId.New(),
                TestPlanId.New(),
                1,
                "Functional test",
                [characteristic],
                QualityTestData.BaseTimeUtc.ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<KeyNotFoundException>(
            () => QualityTestData.CreatePlan(characteristic)
                .GetCharacteristic(TestCharacteristicId.New()));
    }
}
