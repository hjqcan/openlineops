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

    [Fact]
    public void GenerateRejectsCaseChangedJudgementToken()
    {
        var generator = new ConfiguredTraceJudgementGenerator(new TraceJudgementOptions());
        var result = generator.Generate(CreateRequest(
            judgement: "aborted",
            measurements: [CreateMeasurement(passed: true)]));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.InvalidJudgement", result.Error.Code);
        Assert.Contains("case-sensitive", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void GenerateRejectsExplicitBlankJudgement(string judgement)
    {
        var generator = new ConfiguredTraceJudgementGenerator(new TraceJudgementOptions());
        var result = generator.Generate(CreateRequest(
            judgement,
            measurements: [CreateMeasurement(passed: true)]));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.InvalidJudgement", result.Error.Code);
    }

    private static CreateTraceRecordRequest CreateRequest(
        string? judgement,
        IReadOnlyCollection<CreateMeasurementRecordRequest> measurements)
    {
        return new CreateTraceRecordRequest(
            ProductionRunId: Guid.NewGuid(),
            ProjectId: "project-judgement",
            ApplicationId: "application-judgement",
            ProjectSnapshotId: "snapshot-judgement",
            TopologyId: "topology-judgement",
            ProductionLineDefinitionId: "line-judgement",
            DutModelId: "dut-judgement",
            DutIdentityInputKey: "serialNumber",
            DutIdentityValue: "SMX-JUDGEMENT",
            BatchId: "batch-judgement",
            FixtureId: "fixture-judgement",
            DeviceId: "device-judgement",
            ActorId: "operator-judgement",
            RunStatus: "Completed",
            Judgement: judgement,
            CreatedAtUtc: BaseTimeUtc,
            StartedAtUtc: BaseTimeUtc,
            CompletedAtUtc: BaseTimeUtc.AddMinutes(1),
            FailureCode: null,
            FailureReason: null,
            Stages:
            [
                new CreateTraceStageExecutionRequest(
                    StageId: "stage-judgement",
                    Sequence: 1,
                    WorkstationId: "workstation-judgement",
                    StationId: "station-judgement",
                    ProcessDefinitionId: "process-judgement",
                    ProcessVersionId: "process-judgement@1.0.0",
                    ConfigurationSnapshotId: "config-judgement",
                    RecipeSnapshotId: "recipe-judgement",
                    RuntimeSessionId: Guid.NewGuid(),
                    RuntimeSessionStatus: "Completed",
                    Status: "Completed",
                    StartedAtUtc: BaseTimeUtc,
                    CompletedAtUtc: BaseTimeUtc.AddMinutes(1),
                    FailureCode: null,
                    FailureReason: null,
                    CompletedStepCount: 0,
                    CommandCount: 0,
                    IncidentCount: 0,
                    Commands: [],
                    Measurements: measurements,
                    Artifacts: [],
                    Incidents: [])
            ],
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
            ActionId: "action.measure.voltage",
            TargetKind: "System",
            TargetId: "system.measurement",
            CommandStatus: passed == false ? "Failed" : "Completed",
            Passed: passed,
            MeasuredAtUtc: BaseTimeUtc.AddSeconds(30));
    }
}
