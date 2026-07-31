using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Quality.Domain.Identifiers;

namespace OpenLineOps.Quality.Domain.TestPlans;

public sealed class TestPlanRevision : AggregateRoot<TestPlanRevisionId>
{
    private readonly List<TestCharacteristic> _characteristics;

    private TestPlanRevision(
        TestPlanRevisionId id,
        TestPlanId testPlanId,
        int revisionNumber,
        string displayName,
        IEnumerable<TestCharacteristic> characteristics,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(characteristics);

        var frozenCharacteristics = characteristics.ToList();
        if (frozenCharacteristics.Count == 0)
        {
            throw new ArgumentException(
                "A test plan revision must contain at least one characteristic.",
                nameof(characteristics));
        }

        if (!frozenCharacteristics.Any(characteristic => characteristic.IsRequired))
        {
            throw new ArgumentException(
                "A test plan revision must contain at least one required characteristic.",
                nameof(characteristics));
        }

        if (frozenCharacteristics.Select(characteristic => characteristic.Id).Distinct().Count()
            != frozenCharacteristics.Count)
        {
            throw new ArgumentException("Characteristic identifiers must be unique.", nameof(characteristics));
        }

        if (frozenCharacteristics
            .Select(characteristic => characteristic.Code)
            .Distinct(StringComparer.Ordinal)
            .Count() != frozenCharacteristics.Count)
        {
            throw new ArgumentException("Characteristic codes must be unique.", nameof(characteristics));
        }

        TestPlanId = testPlanId;
        RevisionNumber = QualityGuard.Positive(revisionNumber, nameof(revisionNumber));
        DisplayName = QualityGuard.DisplayText(displayName, nameof(displayName));
        CreatedAtUtc = QualityGuard.Utc(createdAtUtc, nameof(createdAtUtc));
        _characteristics = frozenCharacteristics;
    }

    public TestPlanId TestPlanId { get; }

    public int RevisionNumber { get; }

    public string DisplayName { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyCollection<TestCharacteristic> Characteristics => _characteristics.AsReadOnly();

    public static TestPlanRevision Create(
        TestPlanRevisionId id,
        TestPlanId testPlanId,
        int revisionNumber,
        string displayName,
        IEnumerable<TestCharacteristic> characteristics,
        DateTimeOffset createdAtUtc)
    {
        return new TestPlanRevision(
            id,
            testPlanId,
            revisionNumber,
            displayName,
            characteristics,
            createdAtUtc);
    }

    public TestCharacteristic GetCharacteristic(TestCharacteristicId characteristicId)
    {
        return _characteristics.SingleOrDefault(characteristic => characteristic.Id == characteristicId)
            ?? throw new KeyNotFoundException(
                $"Characteristic {characteristicId} is not part of test plan revision {Id}.");
    }
}
