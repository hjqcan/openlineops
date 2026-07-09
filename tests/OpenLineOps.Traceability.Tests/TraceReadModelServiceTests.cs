using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.ReadModels;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.Traceability.Tests;

public sealed class TraceReadModelServiceTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetStationDashboardAsyncReturnsCountsAndRecentTraces()
    {
        var repository = new InMemoryTraceRecordRepository();
        var service = new TraceReadModelService(repository);
        var passed = CreateTrace("00000000-0000-0000-0000-000000000101", "SMX-DASH-1", "station-dashboard", ResultJudgement.Passed, BaseTimeUtc.AddMinutes(1));
        var failed = CreateTrace("00000000-0000-0000-0000-000000000102", "SMX-DASH-2", "station-dashboard", ResultJudgement.Failed, BaseTimeUtc.AddMinutes(2), failedMeasurements: 1);
        var otherStation = CreateTrace("00000000-0000-0000-0000-000000000103", "SMX-DASH-3", "station-other", ResultJudgement.Aborted, BaseTimeUtc.AddMinutes(3));

        await repository.SaveAsync(passed);
        await repository.SaveAsync(failed);
        await repository.SaveAsync(otherStation);

        var result = await service.GetStationDashboardAsync(new StationTraceDashboardQuery("station-dashboard", RecentLimit: 2));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(2, result.Value.TotalCount);
        Assert.Equal(1, result.Value.PassedCount);
        Assert.Equal(1, result.Value.FailedCount);
        Assert.Equal(0, result.Value.AbortedCount);
        Assert.Equal(BaseTimeUtc.AddMinutes(1), result.Value.FirstCompletedAtUtc);
        Assert.Equal(BaseTimeUtc.AddMinutes(2), result.Value.LastCompletedAtUtc);
        Assert.Equal(["SMX-DASH-2", "SMX-DASH-1"], result.Value.RecentTraces.Select(trace => trace.SerialNumber));
        Assert.Equal(1, result.Value.RecentTraces.First().FailedMeasurementCount);
    }

    [Fact]
    public async Task SearchForEngineeringAsyncFiltersRowsAndBuildsFacets()
    {
        var repository = new InMemoryTraceRecordRepository();
        var service = new TraceReadModelService(repository);
        var first = CreateTrace("00000000-0000-0000-0000-000000000201", "SMX-ENG-1", "station-a", ResultJudgement.Passed, BaseTimeUtc.AddMinutes(1), processVersionId: "process-a@1.0.0");
        var second = CreateTrace("00000000-0000-0000-0000-000000000202", "SMX-ENG-2", "station-b", ResultJudgement.Failed, BaseTimeUtc.AddMinutes(2), processVersionId: "process-a@1.0.0", failedMeasurements: 1);
        var ignored = CreateTrace("00000000-0000-0000-0000-000000000203", "SMX-ENG-3", "station-a", ResultJudgement.Passed, BaseTimeUtc.AddMinutes(3), processVersionId: "process-b@1.0.0");

        await repository.SaveAsync(first);
        await repository.SaveAsync(second);
        await repository.SaveAsync(ignored);

        var result = await service.SearchForEngineeringAsync(new EngineeringTraceSearchQuery(
            ProcessVersionId: "process-a@1.0.0",
            Paging: new PagedRequest(1, 10)));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(2, result.Value.Results.TotalCount);
        Assert.Equal(["SMX-ENG-1", "SMX-ENG-2"], result.Value.Results.Items.Select(row => row.SerialNumber));
        Assert.Equal(2, Assert.Single(result.Value.Facets.ProcessVersions).Count);
        Assert.Equal(["Failed", "Passed"], result.Value.Facets.Judgements.Select(facet => facet.Value).Order());
        Assert.Equal(1, result.Value.Results.Items.Single(row => row.SerialNumber == "SMX-ENG-2").FailedMeasurementCount);
    }

    private static TraceRecord CreateTrace(
        string traceRecordId,
        string serialNumber,
        string stationId,
        ResultJudgement judgement,
        DateTimeOffset completedAtUtc,
        string processVersionId = "process-a@1.0.0",
        int failedMeasurements = 0)
    {
        var traceRecord = TraceRecord.CreateCompleted(
            new TraceRecordId(Guid.Parse(traceRecordId)),
            new RuntimeSessionId(Guid.NewGuid()),
            serialNumber,
            "batch-read-model",
            new StationId(stationId),
            "fixture-read-model",
            new ProcessDefinitionId("process-a"),
            new ProcessVersionId(processVersionId),
            new ConfigurationSnapshotId("config-read-model"),
            new RecipeSnapshotId("recipe-read-model"),
            new DeviceId("device-read-model"),
            judgement,
            BaseTimeUtc,
            completedAtUtc,
            new ActorId("operator-read-model"));

        traceRecord.AddMeasurement(new MeasurementRecord(
            MeasurementRecordId.New(),
            "voltage",
            3.3m,
            null,
            "V",
            new DeviceId("device-read-model"),
            new RuntimeCommandId(Guid.NewGuid()),
            failedMeasurements == 0,
            completedAtUtc.AddSeconds(-5)));
        traceRecord.AttachArtifact(new ArtifactRecord(
            ArtifactRecordId.New(),
            "log",
            ArtifactKind.Log,
            $"trace/{serialNumber}/log.txt",
            "text/plain",
            128,
            null,
            new DeviceId("device-read-model"),
            completedAtUtc));

        return traceRecord;
    }
}
