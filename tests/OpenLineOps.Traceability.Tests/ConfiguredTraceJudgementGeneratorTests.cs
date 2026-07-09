using OpenLineOps.Traceability.Application.Judgements;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Tests;

public sealed class ConfiguredTraceJudgementGeneratorTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GenerateUsesExplicitJudgementWhenProvided()
    {
        var generator = new ConfiguredTraceJudgementGenerator(new TraceJudgementOptions());
        var request = CreateRequest(
            judgement: "Aborted",
            measurements:
            [
                CreateMeasurement(passed: false)
            ]);

        var result = generator.Generate(request);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(ResultJudgement.Aborted, result.Value);
    }

    [Fact]
    public void GenerateFailsTraceWhenAnyMeasurementFailedByDefault()
    {
        var generator = new ConfiguredTraceJudgementGenerator(new TraceJudgementOptions());
        var request = CreateRequest(
            judgement: null,
            measurements:
            [
                CreateMeasurement(passed: true),
                CreateMeasurement(passed: false)
            ]);

        var result = generator.Generate(request);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(ResultJudgement.Failed, result.Value);
    }

    [Fact]
    public void GenerateCanUseConfiguredUnknownForNoMeasurements()
    {
        var generator = new ConfiguredTraceJudgementGenerator(new TraceJudgementOptions
        {
            UnknownWhenNoMeasurements = true
        });
        var request = CreateRequest(judgement: null, measurements: []);

        var result = generator.Generate(request);

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(ResultJudgement.Unknown, result.Value);
    }

    [Fact]
    public void GenerateRejectsUnsupportedConfiguredDefaultJudgement()
    {
        var generator = new ConfiguredTraceJudgementGenerator(new TraceJudgementOptions
        {
            DefaultJudgement = "Unsupported"
        });
        var request = CreateRequest(
            judgement: null,
            measurements:
            [
                CreateMeasurement(passed: true)
            ]);

        var result = generator.Generate(request);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.InvalidDefaultJudgement", result.Error.Code);
    }

    private static CreateTraceRecordRequest CreateRequest(
        string? judgement,
        IReadOnlyCollection<CreateMeasurementRecordRequest> measurements)
    {
        return new CreateTraceRecordRequest(
            TraceRecordId: null,
            RuntimeSessionId: Guid.NewGuid(),
            SerialNumber: "SMX-JUDGEMENT",
            BatchId: "batch-judgement",
            StationId: "station-judgement",
            FixtureId: "fixture-judgement",
            ProcessDefinitionId: "process-judgement",
            ProcessVersionId: "process-judgement@1.0.0",
            ConfigurationSnapshotId: "config-judgement",
            RecipeSnapshotId: "recipe-judgement",
            DeviceId: "device-judgement",
            Judgement: judgement,
            StartedAtUtc: BaseTimeUtc,
            CompletedAtUtc: BaseTimeUtc.AddMinutes(1),
            RecordedBy: "operator-judgement",
            Measurements: measurements,
            Artifacts: [],
            AuditEntries: []);
    }

    private static CreateMeasurementRecordRequest CreateMeasurement(bool? passed)
    {
        return new CreateMeasurementRecordRequest(
            MeasurementRecordId: null,
            Name: "voltage",
            NumericValue: 3.3m,
            TextValue: null,
            Unit: "V",
            DeviceId: "device-judgement",
            RuntimeCommandId: Guid.NewGuid(),
            Passed: passed,
            MeasuredAtUtc: BaseTimeUtc.AddSeconds(30));
    }
}
