using Microsoft.Data.Sqlite;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Quality.Application.Contracts;
using OpenLineOps.Quality.Application.Services;
using OpenLineOps.Quality.Domain.Calibration;
using OpenLineOps.Quality.Domain.Nonconformances;
using OpenLineOps.Quality.Domain.Testing;
using OpenLineOps.Quality.Infrastructure.Persistence;

namespace OpenLineOps.Quality.Tests;

public sealed class QualityServicePersistenceTests : IDisposable
{
    private const string ActorId = "quality-engineer-01";
    private const string AgentId = "station-agent-01";
    private const string StationId = "station-functional-test";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-quality-tests-{Guid.NewGuid():N}");
    private readonly TestClock _clock = new(QualityTestData.BaseTimeUtc);

    [Fact]
    public async Task CreateIsIdempotentAndRejectsChangedEvidenceForSameKey()
    {
        Directory.CreateDirectory(_root);
        using var repository = CreateRepository();
        var service = new QualityService(repository, _clock);
        var command = CreatePlanCommand("plan-create-01");

        var first = await service.CreateTestPlanRevisionAsync(command);
        var replay = await service.CreateTestPlanRevisionAsync(command);
        var conflict = await service.CreateTestPlanRevisionAsync(
            command with { DisplayName = "Changed plan" });
        var all = await service.ListTestPlanRevisionsAsync();

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value.TestPlanRevisionId, replay.Value.TestPlanRevisionId);
        Assert.Equal(first.Value.ResourceRevision, replay.Value.ResourceRevision);
        Assert.Equal(
            first.Value.Characteristics.Single().TestCharacteristicId,
            replay.Value.Characteristics.Single().TestCharacteristicId);
        Assert.True(conflict.IsFailure);
        Assert.StartsWith("Conflict.", conflict.Error.Code, StringComparison.Ordinal);
        Assert.Single(all.Value);
    }

    [Fact]
    public async Task MeasurementAppendUsesOptimisticRevisionAndIdempotency()
    {
        Directory.CreateDirectory(_root);
        using var repository = CreateRepository();
        var service = new QualityService(repository, _clock);
        var seeded = await SeedRunningAttemptAsync(service);
        var measurement = CreateMeasurementCommand(
            seeded,
            expectedRevision: 1,
            normalizedValue: 5.1m,
            "measurement-append-01");

        var first = await service.AppendMeasurementAsync(measurement);
        var replay = await service.AppendMeasurementAsync(measurement);
        var stale = await service.AppendMeasurementAsync(
            measurement with
            {
                MeasurementResultId = Guid.NewGuid(),
                IdempotencyKey = "measurement-append-stale"
            });

        Assert.True(first.IsSuccess);
        Assert.Equal(2, first.Value.ResourceRevision);
        Assert.Single(first.Value.Measurements);
        Assert.Equal(MeasurementJudgement.Passed, first.Value.Measurements.Single().Judgement);
        Assert.True(replay.IsSuccess);
        Assert.Single(replay.Value.Measurements);
        Assert.True(stale.IsFailure);
        Assert.Equal("Conflict.Quality.Revision.Mismatch", stale.Error.Code);
    }

    [Fact]
    public async Task CompletionRejectsMissingRequiredMeasurements()
    {
        Directory.CreateDirectory(_root);
        using var repository = CreateRepository();
        var service = new QualityService(repository, _clock);
        var seeded = await SeedRunningAttemptAsync(service);

        var result = await service.CompleteTestAttemptAsync(
            new CompleteTestAttemptCommand(
                seeded.TestAttemptId,
                ExpectedRevision: 1,
                QualityTestData.BaseTimeUtc.AddSeconds(2),
                StationId,
                AgentId,
                "attempt-complete-missing"));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Quality.TestAttempt.CompletionConflict", result.Error.Code);
        var persisted = await service.GetTestAttemptAsync(seeded.TestAttemptId);
        Assert.Equal(TestAttemptStatus.Running, persisted.Value.Status);
        Assert.Equal(1, persisted.Value.ResourceRevision);
    }

    [Fact]
    public async Task ExpiredCalibrationCreatesInvalidFactAndOpenNonconformance()
    {
        Directory.CreateDirectory(_root);
        using var repository = CreateRepository();
        var service = new QualityService(repository, _clock);
        var seeded = await SeedRunningAttemptAsync(
            service,
            calibrationValidUntilUtc: QualityTestData.BaseTimeUtc);

        var appended = await service.AppendMeasurementAsync(
            CreateMeasurementCommand(
                seeded,
                expectedRevision: 1,
                normalizedValue: 5m,
                "measurement-expired-calibration"));
        var nonconformances = await service.ListNonconformancesAsync(
            StationId,
            seeded.ProductionUnitId,
            NonconformanceStatus.Open);

        Assert.True(appended.IsSuccess);
        var measurement = Assert.Single(appended.Value.Measurements);
        Assert.Equal(CalibrationStatus.Expired, measurement.CalibrationStatus);
        Assert.Equal(MeasurementJudgement.Invalid, measurement.Judgement);
        var nonconformance = Assert.Single(nonconformances.Value);
        Assert.Equal(NonconformanceSeverity.Critical, nonconformance.Severity);
        Assert.Equal(measurement.MeasurementResultId, nonconformance.MeasurementResultId);
    }

    [Fact]
    public async Task InvalidEvidenceHashDoesNotAppendAProductionFact()
    {
        Directory.CreateDirectory(_root);
        using var repository = CreateRepository();
        var service = new QualityService(repository, _clock);
        var seeded = await SeedRunningAttemptAsync(service);

        var result = await service.AppendMeasurementAsync(
            CreateMeasurementCommand(
                    seeded,
                    expectedRevision: 1,
                    normalizedValue: 5m,
                    "measurement-invalid-evidence")
                with
            {
                EvidenceSha256 = "not-a-digest"
            });

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Quality.Measurement.Invalid", result.Error.Code);
        var persisted = await service.GetTestAttemptAsync(seeded.TestAttemptId);
        Assert.Empty(persisted.Value.Measurements);
        Assert.Equal(1, persisted.Value.ResourceRevision);
    }

    [Fact]
    public async Task AppendOnlyFactsSurviveRepositoryRestartAndDisposition()
    {
        Directory.CreateDirectory(_root);
        var seeded = default(SeededAttempt);
        Guid nonconformanceId;

        using (var firstRepository = CreateRepository())
        {
            var firstService = new QualityService(firstRepository, _clock);
            seeded = await SeedRunningAttemptAsync(firstService);
            var appended = await firstService.AppendMeasurementAsync(
                CreateMeasurementCommand(
                    seeded,
                    expectedRevision: 1,
                    normalizedValue: 6m,
                    "measurement-before-restart"));
            Assert.Equal(MeasurementJudgement.Failed, appended.Value.Measurements.Single().Judgement);
            nonconformanceId = (await firstService.ListNonconformancesAsync(
                    StationId,
                    seeded.ProductionUnitId,
                    NonconformanceStatus.Open))
                .Value
                .Single()
                .NonconformanceId;
        }

        using (var secondRepository = CreateRepository())
        {
            var secondService = new QualityService(secondRepository, _clock);
            var recovered = await secondService.GetTestAttemptAsync(seeded.TestAttemptId);
            _clock.UtcNow = QualityTestData.BaseTimeUtc.AddSeconds(2);
            var dispositioned = await secondService.DispositionNonconformanceAsync(
                new DispositionNonconformanceCommand(
                    nonconformanceId,
                    ExpectedRevision: 1,
                    NonconformanceDisposition.Rework,
                    "Inspect the connector and repeat once.",
                    ActorId,
                    "nonconformance-disposition-01"));

            Assert.True(recovered.IsSuccess);
            Assert.Equal(2, recovered.Value.ResourceRevision);
            Assert.Single(recovered.Value.Measurements);
            Assert.True(dispositioned.IsSuccess, dispositioned.Error.ToString());
            Assert.Equal(2, dispositioned.Value.ResourceRevision);
            Assert.Equal(NonconformanceStatus.Dispositioned, dispositioned.Value.Status);
        }

        using var thirdRepository = CreateRepository();
        var thirdService = new QualityService(thirdRepository, _clock);
        var persistedDisposition = await thirdService.GetNonconformanceAsync(nonconformanceId);
        Assert.Equal(NonconformanceDisposition.Rework, persistedDisposition.Value.Disposition);
        Assert.Equal("Inspect the connector and repeat once.", persistedDisposition.Value.DispositionReason);
    }

    [Fact]
    public void PersistenceOptionsRequireCanonicalFileBackedSqlite()
    {
        var validator = new QualityPersistenceOptionsValidator();

        var defaultResult = validator.Validate(name: null, new QualityPersistenceOptions());
        var providerResult = validator.Validate(
            name: null,
            new QualityPersistenceOptions { Provider = "sqlite" });
        var memoryResult = validator.Validate(
            name: null,
            new QualityPersistenceOptions { ConnectionString = "Data Source=:memory:" });
        var paddedPathResult = validator.Validate(
            name: null,
            new QualityPersistenceOptions { DatabasePath = " data/quality.sqlite" });

        Assert.True(defaultResult.Succeeded);
        Assert.True(providerResult.Failed);
        Assert.True(memoryResult.Failed);
        Assert.True(paddedPathResult.Failed);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        var resolvedRoot = Path.GetFullPath(_root);
        var resolvedTemp = Path.GetFullPath(Path.GetTempPath());
        if (!resolvedRoot.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolvedRoot).StartsWith(
                "openlineops-quality-tests-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete an unexpected test directory.");
        }

        Directory.Delete(resolvedRoot, recursive: true);
    }

    private SqliteQualityRepository CreateRepository()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "quality.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
        return new SqliteQualityRepository(connectionString);
    }

    private static async Task<SeededAttempt> SeedRunningAttemptAsync(
        QualityService service,
        DateTimeOffset? calibrationValidUntilUtc = null)
    {
        var plan = CreatePlanCommand($"plan-create-{Guid.NewGuid():N}");
        Assert.True((await service.CreateTestPlanRevisionAsync(plan)).IsSuccess);

        var calibrationId = Guid.NewGuid();
        var calibration = await service.CreateCalibrationAssetAsync(
            new CreateCalibrationAssetCommand(
                calibrationId,
                $"asset-{calibrationId:N}",
                $"instrument-{calibrationId:N}",
                $"certificate-{calibrationId:N}",
                QualityTestData.BaseTimeUtc.AddDays(-2),
                calibrationValidUntilUtc ?? QualityTestData.BaseTimeUtc.AddDays(30),
                ActorId,
                $"calibration-create-{Guid.NewGuid():N}"));
        Assert.True(calibration.IsSuccess);

        var attemptId = Guid.NewGuid();
        var productionUnitId = $"unit-{attemptId:N}";
        var attempt = await service.CreateTestAttemptAsync(
            new CreateTestAttemptCommand(
                attemptId,
                plan.TestPlanRevisionId,
                productionUnitId,
                AttemptNumber: 1,
                QualityTestData.BaseTimeUtc,
                StationId,
                AgentId,
                $"attempt-create-{Guid.NewGuid():N}"));
        Assert.True(attempt.IsSuccess);

        return new SeededAttempt(
            attemptId,
            plan.TestPlanRevisionId,
            plan.Characteristics.Single().TestCharacteristicId,
            calibrationId,
            productionUnitId);
    }

    private static CreateTestPlanRevisionCommand CreatePlanCommand(string idempotencyKey)
    {
        var characteristicId = Guid.NewGuid();
        return new CreateTestPlanRevisionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RevisionNumber: 1,
            "Functional test",
            [
                new CreateTestCharacteristicCommand(
                    characteristicId,
                    "supply.voltage",
                    "Supply voltage",
                    "V",
                    "step-voltage@1",
                    IsRequired: true,
                    new CreateLimitSetCommand(
                        Guid.NewGuid(),
                        "V",
                        LowerLimit: 4.5m,
                        LowerInclusive: true,
                        UpperLimit: 5.5m,
                        UpperInclusive: true))
            ],
            ActorId,
            idempotencyKey);
    }

    private static AppendMeasurementCommand CreateMeasurementCommand(
        SeededAttempt seeded,
        int expectedRevision,
        decimal normalizedValue,
        string idempotencyKey) =>
        new(
            seeded.TestAttemptId,
            expectedRevision,
            Guid.NewGuid(),
            seeded.TestCharacteristicId,
            normalizedValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            normalizedValue,
            seeded.CalibrationAssetId,
            "step-voltage@1",
            QualityTestData.EvidenceSha256,
            QualityTestData.BaseTimeUtc.AddSeconds(1),
            StationId,
            AgentId,
            idempotencyKey);

    private sealed record SeededAttempt(
        Guid TestAttemptId,
        Guid TestPlanRevisionId,
        Guid TestCharacteristicId,
        Guid CalibrationAssetId,
        string ProductionUnitId);

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
