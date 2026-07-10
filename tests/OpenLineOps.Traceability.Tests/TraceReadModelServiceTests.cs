using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.ReadModels;
using OpenLineOps.Traceability.Domain.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.Traceability.Tests;

public sealed class TraceReadModelServiceTests
{
    [Fact]
    public async Task StationDashboardCountsProductionRunsAndAggregatesMatchingStageEvidence()
    {
        var repository = new InMemoryTraceRecordRepository();
        var service = new TraceReadModelService(repository);
        await repository.TryAddAsync(TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000101",
            "SMX-DASH-1",
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            stationId: "station-dashboard"));
        await repository.TryAddAsync(TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000102",
            "SMX-DASH-2",
            TraceTestData.BaseTimeUtc.AddMinutes(2),
            stationId: "station-dashboard",
            judgement: ResultJudgement.Failed));
        await repository.TryAddAsync(TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000103",
            "SMX-DASH-3",
            TraceTestData.BaseTimeUtc.AddMinutes(3),
            stationId: "station-other"));

        var result = await service.GetStationDashboardAsync(
            new StationTraceDashboardQuery("station-dashboard", RecentLimit: 2));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(2, result.Value.TotalCount);
        Assert.Equal(1, result.Value.PassedCount);
        Assert.Equal(1, result.Value.FailedCount);
        Assert.Equal(["SMX-DASH-2", "SMX-DASH-1"],
            result.Value.RecentTraces.Select(trace => trace.DutIdentityValue));
        Assert.Equal(1, result.Value.RecentTraces.First().FailedMeasurementCount);
        Assert.Equal(1, result.Value.RecentTraces.First().StageCount);
    }

    [Fact]
    public async Task EngineeringSearchFiltersNestedStageAndBuildsRunFacets()
    {
        var repository = new InMemoryTraceRecordRepository();
        var service = new TraceReadModelService(repository);
        await repository.TryAddAsync(TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000201",
            "SMX-ENG-1",
            TraceTestData.BaseTimeUtc.AddMinutes(1),
            stationId: "station-a",
            processVersionId: "process-a@1.0.0",
            projectSnapshotId: "snapshot-read-model"));
        await repository.TryAddAsync(TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000202",
            "SMX-ENG-2",
            TraceTestData.BaseTimeUtc.AddMinutes(2),
            stationId: "station-b",
            processVersionId: "process-a@1.0.0",
            judgement: ResultJudgement.Failed,
            projectSnapshotId: "snapshot-read-model"));
        await repository.TryAddAsync(TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000203",
            "SMX-ENG-3",
            TraceTestData.BaseTimeUtc.AddMinutes(3),
            stationId: "station-a",
            processVersionId: "process-a@1.0.0",
            projectSnapshotId: "snapshot-other"));

        var result = await service.SearchForEngineeringAsync(new EngineeringTraceSearchQuery(
            ProcessVersionId: "process-a@1.0.0",
            ProjectSnapshotId: "snapshot-read-model",
            Paging: new PagedRequest(1, 10)));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(2, result.Value.Results.TotalCount);
        Assert.Equal(["SMX-ENG-1", "SMX-ENG-2"],
            result.Value.Results.Items.Select(row => row.DutIdentityValue));
        Assert.Equal(2, Assert.Single(result.Value.Facets.ProcessVersions).Count);
        Assert.Equal("snapshot-read-model", Assert.Single(result.Value.Facets.ProjectSnapshots).Value);
        Assert.Equal(["Failed", "Passed"],
            result.Value.Facets.Judgements.Select(facet => facet.Value).Order());
        var failedRow = result.Value.Results.Items.Single(row => row.DutIdentityValue == "SMX-ENG-2");
        Assert.Equal(1, failedRow.FailedMeasurementCount);
        Assert.Equal("Failed", failedRow.RunStatus);
    }

    [Theory]
    [InlineData(" station-a")]
    [InlineData("station-a ")]
    public async Task StationDashboardRejectsNonCanonicalStationId(string stationId)
    {
        var service = new TraceReadModelService(new InMemoryTraceRecordRepository());
        var result = await service.GetStationDashboardAsync(new StationTraceDashboardQuery(stationId));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.StationIdNotCanonical", result.Error.Code);
    }
}
