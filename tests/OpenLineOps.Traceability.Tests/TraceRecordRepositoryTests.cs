using System.Text.Json.Nodes;
using OpenLineOps.Runtime.Contracts;
using Microsoft.Data.Sqlite;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.Traceability.Tests;

public sealed class TraceRecordRepositoryTests
{
    [Fact]
    public async Task InMemoryTryAddIsAtomicForDeterministicProductionRunIdentity()
    {
        var repository = new InMemoryTraceRecordRepository();
        var trace = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000001",
            "SMX-0001",
            TraceTestData.BaseTimeUtc.AddMinutes(1));

        Assert.True(await repository.TryAddAsync(trace));
        Assert.False(await repository.TryAddAsync(trace));
        Assert.Equal(1, repository.AddCount);
        Assert.Equal(1, (await repository.QueryAsync(new TraceRecordQuery())).TotalCount);
    }

    [Fact]
    public async Task InMemoryQueryUsesStablePaginationByRunCompletionAndIdentity()
    {
        var repository = new InMemoryTraceRecordRepository();
        var first = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000001", "SMX-0001", TraceTestData.BaseTimeUtc.AddMinutes(2));
        var second = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000002", "SMX-0002", TraceTestData.BaseTimeUtc.AddMinutes(2));
        var third = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000003", "SMX-0003", TraceTestData.BaseTimeUtc.AddMinutes(3));
        await repository.TryAddAsync(third);
        await repository.TryAddAsync(second);
        await repository.TryAddAsync(first);

        var pageOne = await repository.QueryAsync(new TraceRecordQuery(paging: new PagedRequest(1, 2)));
        var pageTwo = await repository.QueryAsync(new TraceRecordQuery(paging: new PagedRequest(2, 2)));

        Assert.Equal(3, pageOne.TotalCount);
        Assert.Equal(
            ["SMX-0001", "SMX-0002"],
            pageOne.Items.Select(item => item.ProductionUnitIdentityValue));
        Assert.Equal(["SMX-0003"], pageTwo.Items.Select(item => item.ProductionUnitIdentityValue));
    }

    [Fact]
    public async Task SqlitePersistsWholeProductionRunOperationEvidenceAcrossRestart()
    {
        using var database = TemporarySqliteDatabase.Create();
        var trace = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000031",
            "SMX-1001",
            TraceTestData.BaseTimeUtc.AddMinutes(5));
        using (var repository = new SqliteTraceRecordRepository(database.ConnectionString))
        {
            Assert.True(await repository.TryAddAsync(trace));
            Assert.False(await repository.TryAddAsync(trace));
        }

        using var restarted = new SqliteTraceRecordRepository(database.ConnectionString);
        var restored = await restarted.GetByIdAsync(trace.Id);

        Assert.NotNull(restored);
        Assert.Equal(restored.Id.Value, restored.ProductionRunId.Value);
        Assert.Equal("SMX-1001", restored.ProductionUnitIdentityValue);
        Assert.Equal("line-a", restored.ProductionLineDefinitionId);
        var operation = Assert.Single(restored.Operations);
        Assert.Equal("operation.inspect", operation.OperationId);
        var command = Assert.Single(operation.Commands);
        Assert.Equal(ResultJudgement.Passed, command.ResultJudgement);
        Assert.Single(operation.Measurements);
        Assert.Single(operation.Artifacts);
        Assert.Single(operation.Outputs);
        Assert.Equal(2, operation.FencingTokens.Count);
        Assert.Single(restored.Genealogy);
        Assert.Single(restored.MaterialLocationTransitions);
        Assert.Single(restored.SlotOccupancyTransitions);
        Assert.Single(restored.DispositionTransitions);
        Assert.Equal(
            "station-a",
            Assert.Single(restored.MaterialLocationTransitions).Destination.StationSystemId);
        Assert.Equal(
            ProductDisposition.Completed,
            Assert.Single(restored.DispositionTransitions).CurrentDisposition);
        Assert.Single(restored.AuditEntries);
    }

    [Fact]
    public async Task SqliteQueryFiltersRootAndSameNestedOperationThenUsesStablePagination()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteTraceRecordRepository(database.ConnectionString);
        var match = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000021",
            "SMX-3001",
            TraceTestData.BaseTimeUtc.AddMinutes(2),
            processVersionId: "process-packaging@match",
            deviceId: "device-match",
            judgement: ResultJudgement.Failed,
            projectSnapshotId: "snapshot-match");
        var wrongProcess = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000022",
            "SMX-3002",
            TraceTestData.BaseTimeUtc.AddMinutes(3),
            processVersionId: "process-packaging@other",
            deviceId: "device-match",
            judgement: ResultJudgement.Failed,
            projectSnapshotId: "snapshot-match");
        var wrongDevice = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000023",
            "SMX-3003",
            TraceTestData.BaseTimeUtc.AddMinutes(4),
            processVersionId: "process-packaging@match",
            deviceId: "device-other",
            judgement: ResultJudgement.Failed,
            projectSnapshotId: "snapshot-match");
        await repository.TryAddAsync(wrongDevice);
        await repository.TryAddAsync(wrongProcess);
        await repository.TryAddAsync(match);

        var result = await repository.QueryAsync(new TraceRecordQuery(
            productionUnitIdentityValue: "SMX-3001",
            processVersionId: "process-packaging@match",
            deviceId: "device-match",
            judgement: "Failed",
            projectSnapshotId: "snapshot-match",
            paging: new PagedRequest(1, 10)));

        var stored = Assert.Single(result.Items);
        Assert.Equal("SMX-3001", stored.ProductionUnitIdentityValue);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task SqliteSnapshotRequiresExactCanonicalExecutionStatusToken()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteTraceRecordRepository(database.ConnectionString);
        var trace = TraceTestData.CreateTrace(
            "00000000-0000-0000-0000-000000000041",
            "SMX-CANONICAL",
            TraceTestData.BaseTimeUtc.AddMinutes(5));
        await repository.TryAddAsync(trace);

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT document_json FROM trace_records WHERE trace_id = $trace_id;";
        select.Parameters.AddWithValue("$trace_id", trace.Id.Value.ToString("D"));
        var document = JsonNode.Parse((string)(await select.ExecuteScalarAsync())!)!.AsObject();
        document["executionStatus"] = "completed";
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE trace_records SET document_json = $json WHERE trace_id = $trace_id;";
        update.Parameters.AddWithValue("$json", document.ToJsonString());
        update.Parameters.AddWithValue("$trace_id", trace.Id.Value.ToString("D"));
        await update.ExecuteNonQueryAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetByIdAsync(trace.Id).AsTask());
        Assert.Contains("case-sensitive", exception.Message, StringComparison.Ordinal);
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
            return new TemporarySqliteDatabase(directory, Path.Combine(directory, "trace-records.sqlite"));
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
