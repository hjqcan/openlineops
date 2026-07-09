using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;
using OpenLineOps.Traceability.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresTraceRecordRepositoryIntegrationTests
{
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _postgres;

    public PostgresTraceRecordRepositoryIntegrationTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [PostgresIntegrationFact]
    public async Task SaveAsyncPersistsTraceGraphAndQueryIndexesForNewRepositoryInstance()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var trace = CreateTraceRecord(suffix, ResultJudgement.Failed);
        trace.AddMeasurement(new MeasurementRecord(
            MeasurementRecordId.New(),
            "height",
            3.45m,
            null,
            "mm",
            new DeviceId($"device-{suffix}"),
            new RuntimeCommandId(Guid.NewGuid()),
            false,
            BaseTimeUtc.AddMinutes(1)));
        trace.AttachArtifact(new ArtifactRecord(
            ArtifactRecordId.New(),
            "vision-log",
            ArtifactKind.Log,
            $"trace/SMX-{suffix}/vision.log",
            "text/plain",
            512,
            "sha256-log",
            new DeviceId($"device-{suffix}"),
            BaseTimeUtc.AddMinutes(2)));
        trace.RecordAudit(new AuditEntry(
            AuditEntryId.New(),
            new ActorId("runtime-service"),
            "TraceRecord.Completed",
            "Runtime session completed.",
            BaseTimeUtc.AddMinutes(2)));

        await using (var repository = new PostgresTraceRecordRepository(_postgres.ConnectionString))
        {
            await repository.SaveAsync(trace);
        }

        await using var restartedRepository = new PostgresTraceRecordRepository(_postgres.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(trace.Id);
        var queryResult = await restartedRepository.QueryAsync(new TraceRecordQuery(
            processVersionId: $"process-packaging@{suffix}",
            deviceId: $"device-{suffix}",
            judgement: "Failed",
            paging: new PagedRequest(1, 10)));

        Assert.NotNull(restored);
        Assert.Equal(trace.Id, restored.Id);
        Assert.Equal($"serial-{suffix}", restored.SerialNumber);
        Assert.Equal($"config-snapshot-{suffix}", restored.ConfigurationSnapshotId.Value);
        Assert.Equal($"recipe-snapshot-{suffix}", restored.RecipeSnapshotId.Value);
        Assert.Empty(restored.DomainEvents);

        var restoredMeasurement = Assert.Single(restored.Measurements);
        Assert.Equal("height", restoredMeasurement.Name);
        Assert.False(restoredMeasurement.Passed);

        var restoredArtifact = Assert.Single(restored.Artifacts);
        Assert.Equal($"trace/SMX-{suffix}/vision.log", restoredArtifact.StorageKey);

        var restoredAudit = Assert.Single(restored.AuditEntries);
        Assert.Equal("runtime-service", restoredAudit.ActorId.Value);

        var queried = Assert.Single(queryResult.Items);
        Assert.Equal(trace.Id, queried.Id);
        Assert.Equal(1, queryResult.TotalCount);
    }

    private static TraceRecord CreateTraceRecord(string suffix, ResultJudgement judgement)
    {
        return TraceRecord.CreateCompleted(
            TraceRecordId.New(),
            new RuntimeSessionId(Guid.NewGuid()),
            $"serial-{suffix}",
            $"batch-{suffix}",
            new StationId($"station-{suffix}"),
            $"fixture-{suffix}",
            new ProcessDefinitionId("process-packaging"),
            new ProcessVersionId($"process-packaging@{suffix}"),
            new ConfigurationSnapshotId($"config-snapshot-{suffix}"),
            new RecipeSnapshotId($"recipe-snapshot-{suffix}"),
            new DeviceId($"device-{suffix}"),
            judgement,
            BaseTimeUtc,
            BaseTimeUtc.AddMinutes(2),
            new ActorId("operator-a"));
    }
}
