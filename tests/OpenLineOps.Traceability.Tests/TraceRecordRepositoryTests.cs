using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.Traceability.Tests;

public sealed class TraceRecordRepositoryTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryQueryAsyncUsesStablePagination()
    {
        var repository = new InMemoryTraceRecordRepository();
        var first = CreateTraceWithId("00000000-0000-0000-0000-000000000001", "SMX-0001", BaseTimeUtc.AddMinutes(2));
        var second = CreateTraceWithId("00000000-0000-0000-0000-000000000002", "SMX-0002", BaseTimeUtc.AddMinutes(2));
        var third = CreateTraceWithId("00000000-0000-0000-0000-000000000003", "SMX-0003", BaseTimeUtc.AddMinutes(3));

        await repository.SaveAsync(third);
        await repository.SaveAsync(second);
        await repository.SaveAsync(first);

        var pageOne = await repository.QueryAsync(new TraceRecordQuery(paging: new PagedRequest(1, 2)));
        var pageTwo = await repository.QueryAsync(new TraceRecordQuery(paging: new PagedRequest(2, 2)));

        Assert.Equal(3, pageOne.TotalCount);
        Assert.Equal(["SMX-0001", "SMX-0002"], pageOne.Items.Select(item => item.SerialNumber));
        Assert.Equal(["SMX-0003"], pageTwo.Items.Select(item => item.SerialNumber));
    }

    [Fact]
    public async Task SqliteSaveAsyncPersistsTraceGraphForNewRepositoryInstance()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteTraceRecordRepository(database.ConnectionString);
        var trace = TraceRecordTests.CreateTrace("SMX-1001", BaseTimeUtc.AddMinutes(5));
        trace.AddMeasurement(new MeasurementRecord(
            MeasurementRecordId.New(),
            "height",
            3.45m,
            null,
            "mm",
            new DeviceId("vision-camera-a"),
            new RuntimeCommandId(Guid.NewGuid()),
            true,
            BaseTimeUtc.AddMinutes(4)));
        trace.AttachArtifact(new ArtifactRecord(
            ArtifactRecordId.New(),
            "vision-log",
            ArtifactKind.Log,
            "trace/SMX-1001/vision.log",
            "text/plain",
            512,
            "sha256-log",
            new DeviceId("vision-camera-a"),
            BaseTimeUtc.AddMinutes(5)));
        trace.RecordAudit(new AuditEntry(
            AuditEntryId.New(),
            new ActorId("runtime-service"),
            "TraceRecord.Completed",
            "Runtime session completed.",
            BaseTimeUtc.AddMinutes(5)));

        await repository.SaveAsync(trace);

        using var restartedRepository = new SqliteTraceRecordRepository(database.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(trace.Id);

        Assert.NotNull(restored);
        Assert.Equal(trace.Id, restored.Id);
        Assert.Equal(trace.RuntimeSessionId, restored.RuntimeSessionId);
        Assert.Equal("config-snapshot-a", restored.ConfigurationSnapshotId.Value);
        Assert.Equal("station-a", restored.StationId.Value);
        Assert.Equal("process-packaging@2026.06.29", restored.ProcessVersionId.Value);
        Assert.Empty(restored.DomainEvents);

        var restoredMeasurement = Assert.Single(restored.Measurements);
        Assert.Equal("height", restoredMeasurement.Name);
        Assert.Equal(3.45m, restoredMeasurement.NumericValue);

        var restoredArtifact = Assert.Single(restored.Artifacts);
        Assert.Equal("trace/SMX-1001/vision.log", restoredArtifact.StorageKey);
        Assert.Equal("text/plain", restoredArtifact.MediaType);
        Assert.Equal(512, restoredArtifact.SizeBytes);

        var restoredAudit = Assert.Single(restored.AuditEntries);
        Assert.Equal("runtime-service", restoredAudit.ActorId.Value);
    }

    [Fact]
    public async Task SqliteQueryAsyncFiltersByProcessDeviceAndJudgement()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteTraceRecordRepository(database.ConnectionString);
        var match = CreateTraceWithId(
            "00000000-0000-0000-0000-000000000021",
            "SMX-3001",
            BaseTimeUtc.AddMinutes(2),
            processVersionId: "process-packaging@match",
            deviceId: "device-match",
            judgement: ResultJudgement.Failed);
        var wrongProcess = CreateTraceWithId(
            "00000000-0000-0000-0000-000000000022",
            "SMX-3002",
            BaseTimeUtc.AddMinutes(3),
            processVersionId: "process-packaging@other",
            deviceId: "device-match",
            judgement: ResultJudgement.Failed);
        var wrongDevice = CreateTraceWithId(
            "00000000-0000-0000-0000-000000000023",
            "SMX-3003",
            BaseTimeUtc.AddMinutes(4),
            processVersionId: "process-packaging@match",
            deviceId: "device-other",
            judgement: ResultJudgement.Failed);
        var wrongJudgement = CreateTraceWithId(
            "00000000-0000-0000-0000-000000000024",
            "SMX-3004",
            BaseTimeUtc.AddMinutes(5),
            processVersionId: "process-packaging@match",
            deviceId: "device-match");

        await repository.SaveAsync(wrongJudgement);
        await repository.SaveAsync(wrongDevice);
        await repository.SaveAsync(wrongProcess);
        await repository.SaveAsync(match);

        var result = await repository.QueryAsync(new TraceRecordQuery(
            processVersionId: "process-packaging@match",
            deviceId: "device-match",
            judgement: "Failed",
            paging: new PagedRequest(1, 10)));

        var stored = Assert.Single(result.Items);
        Assert.Equal("SMX-3001", stored.SerialNumber);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task SqliteQueryAsyncFiltersTraceRecordsAndUsesStablePagination()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteTraceRecordRepository(database.ConnectionString);
        var first = CreateTraceWithId("00000000-0000-0000-0000-000000000011", "SMX-2001", BaseTimeUtc.AddMinutes(2));
        var second = CreateTraceWithId("00000000-0000-0000-0000-000000000012", "SMX-2002", BaseTimeUtc.AddMinutes(2));
        var third = CreateTraceWithId("00000000-0000-0000-0000-000000000013", "SMX-2003", BaseTimeUtc.AddMinutes(4));
        var outsideWindow = CreateTraceWithId("00000000-0000-0000-0000-000000000014", "SMX-2004", BaseTimeUtc.AddMinutes(10));

        await repository.SaveAsync(outsideWindow);
        await repository.SaveAsync(third);
        await repository.SaveAsync(second);
        await repository.SaveAsync(first);

        var query = new TraceRecordQuery(
            batchId: "batch-a",
            stationId: "station-a",
            completedFromUtc: BaseTimeUtc.AddMinutes(1),
            completedToUtc: BaseTimeUtc.AddMinutes(5),
            paging: new PagedRequest(1, 2));
        var pageOne = await repository.QueryAsync(query);
        var pageTwo = await repository.QueryAsync(new TraceRecordQuery(
            batchId: "batch-a",
            stationId: "station-a",
            completedFromUtc: BaseTimeUtc.AddMinutes(1),
            completedToUtc: BaseTimeUtc.AddMinutes(5),
            paging: new PagedRequest(2, 2)));

        Assert.Equal(3, pageOne.TotalCount);
        Assert.Equal(["SMX-2001", "SMX-2002"], pageOne.Items.Select(item => item.SerialNumber));
        Assert.Equal(["SMX-2003"], pageTwo.Items.Select(item => item.SerialNumber));
    }

    private static TraceRecord CreateTraceWithId(
        string traceId,
        string serialNumber,
        DateTimeOffset completedAtUtc,
        string processVersionId = "process-packaging@2026.06.29",
        string deviceId = "vision-camera-a",
        ResultJudgement judgement = ResultJudgement.Passed)
    {
        return TraceRecord.CreateCompleted(
            new TraceRecordId(Guid.Parse(traceId)),
            new RuntimeSessionId(Guid.NewGuid()),
            serialNumber,
            "batch-a",
            new StationId("station-a"),
            "fixture-a",
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId(processVersionId),
            new ConfigurationSnapshotId("config-snapshot-a"),
            new RecipeSnapshotId("recipe-snapshot-a"),
            new DeviceId(deviceId),
            judgement,
            BaseTimeUtc,
            completedAtUtc,
            new ActorId("operator-a"));
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        public string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "trace-records.sqlite");

            return new TemporarySqliteDatabase(directory, databasePath);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
