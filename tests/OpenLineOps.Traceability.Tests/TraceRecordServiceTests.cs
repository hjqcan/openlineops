using OpenLineOps.Traceability.Application.Judgements;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;
using OpenLineOps.Traceability.Infrastructure.Time;

namespace OpenLineOps.Traceability.Tests;

public sealed class TraceRecordServiceTests
{
    private static readonly Guid ProductionRunId = Guid.Parse("90000000-0000-0000-0000-000000000001");
    private static readonly Guid RuntimeSessionId = Guid.Parse("90000000-0000-0000-0000-000000000002");
    private static readonly Guid AuditEntryId = Guid.Parse("90000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task CreateDistinguishesIdenticalReplayFromConflictingEvidenceForSameRun()
    {
        var repository = new InMemoryTraceRecordRepository();
        var service = new TraceRecordService(
            repository,
            new SystemClock(),
            new ConfiguredTraceJudgementGenerator(new TraceJudgementOptions()));
        var request = CreateRequest("DUT-001");

        var created = await service.CreateAsync(request);
        var identicalReplay = await service.CreateAsync(request);
        var conflictingReplay = await service.CreateAsync(CreateRequest("DUT-DIFFERENT"));

        Assert.True(created.IsSuccess, created.Error.Message);
        Assert.True(identicalReplay.IsFailure);
        Assert.Equal(
            "Conflict.Traceability.RecordAlreadyExists",
            identicalReplay.Error.Code);
        Assert.True(conflictingReplay.IsFailure);
        Assert.Equal(
            "Conflict.Traceability.RecordEvidenceConflict",
            conflictingReplay.Error.Code);
        Assert.Equal(1, repository.AddCount);
    }

    private static CreateTraceRecordRequest CreateRequest(string dutIdentityValue)
    {
        return new CreateTraceRecordRequest(
            ProductionRunId,
            "project-a",
            "application-a",
            "snapshot-a",
            "topology-a",
            "line-a",
            "dut-model-a",
            "barcode",
            dutIdentityValue,
            "batch-a",
            "fixture-a",
            "device-a",
            "operator-a",
            "Completed",
            "Passed",
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc,
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            null,
            null,
            [
                new CreateTraceStageExecutionRequest(
                    "stage-a",
                    1,
                    "workstation-a",
                    "station-a",
                    "process-a",
                    "process-a@1.0.0",
                    "configuration-a",
                    "recipe-a",
                    RuntimeSessionId,
                    "Completed",
                    "Completed",
                    TraceTestData.BaseTimeUtc,
                    TraceTestData.BaseTimeUtc.AddMinutes(1),
                    null,
                    null,
                    0,
                    0,
                    0,
                    [],
                    [],
                    [],
                    [])
            ],
            [
                new CreateAuditEntryRequest(
                    AuditEntryId,
                    "operator-a",
                    "ProductionRun.Completed",
                    "Production run trace recorded.",
                    TraceTestData.BaseTimeUtc.AddMinutes(1))
            ]);
    }
}
