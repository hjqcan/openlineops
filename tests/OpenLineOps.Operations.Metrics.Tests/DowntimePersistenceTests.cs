using OpenLineOps.Operations.Metrics.Application.Contracts;

namespace OpenLineOps.Operations.Metrics.Tests;

public sealed class DowntimePersistenceTests
{
    [Fact]
    public async Task SourceClearanceReasonAttributionAndCasAreIndependent()
    {
        using var fixture = OperationsMetricsTestData.CreateStoreFixture();
        var store = fixture.CreateStore();
        var service = fixture.CreateService(
            OperationsMetricsTestData.BaseUtc.AddHours(10),
            store);
        var started = OperationsMetricsTestData.BaseUtc.AddHours(8);
        var opened = await service.OpenDowntimeAsync(new OpenDowntimeCommand(
            "fact-open",
            "downtime-1",
            "station-a",
            "plc-line-stop",
            started,
            "agent-a"));
        Assert.Equal(1, opened.Resource.Revision);

        var reasoned = await service.AttributeDowntimeReasonAsync(
            new AttributeDowntimeReasonCommand(
                "fact-reason",
                "downtime-1",
                "MaterialShortage",
                "Feeder empty",
                started.AddMinutes(5),
                "operator-a",
                ExpectedRevision: 1));
        Assert.Equal(2, reasoned.Resource.Revision);
        Assert.Null(reasoned.Resource.SourceClearedAtUtc);
        var reasonReplay = await service.AttributeDowntimeReasonAsync(
            new AttributeDowntimeReasonCommand(
                "fact-reason",
                "downtime-1",
                "MaterialShortage",
                "Feeder empty",
                started.AddMinutes(5),
                "operator-a",
                ExpectedRevision: 1));
        Assert.Equal(
            OperationsMetricsWriteOutcome.Replayed,
            reasonReplay.Outcome);

        await Assert.ThrowsAsync<OperationsMetricsConflictException>(
            async () => await service.AttributeDowntimeReasonAsync(
                new AttributeDowntimeReasonCommand(
                    "fact-stale",
                    "downtime-1",
                    "Other",
                    null,
                    started.AddMinutes(6),
                    "operator-b",
                    ExpectedRevision: 1)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await service.ClearDowntimeFromSourceAsync(
                new ClearDowntimeFromSourceCommand(
                    "fact-wrong-source",
                    "downtime-1",
                    "station-a",
                    "hmi",
                    started.AddMinutes(10),
                    "agent-a",
                    ExpectedRevision: 2)));

        var cleared = await service.ClearDowntimeFromSourceAsync(
            new ClearDowntimeFromSourceCommand(
                "fact-clear",
                "downtime-1",
                "station-a",
                "plc-line-stop",
                started.AddMinutes(10),
                "agent-a",
                ExpectedRevision: 2));
        Assert.Equal(started.AddMinutes(10), cleared.Resource.SourceClearedAtUtc);
        Assert.Equal("MaterialShortage", cleared.Resource.ReasonCode);

        store.Dispose();
        var restarted = fixture.CreateStore();
        var restored = await restarted.GetDowntimeAsync("downtime-1");
        Assert.NotNull(restored);
        Assert.Equal(3, restored.Revision);
        Assert.Equal(started.AddMinutes(10), restored.SourceClearedAtUtc);
        Assert.Equal("operator-a", restored.ReasonAttributedBy);
    }

    [Fact]
    public async Task ConcurrentFactsUseCompareAndSwap()
    {
        using var fixture = OperationsMetricsTestData.CreateStoreFixture();
        var service = fixture.CreateService(
            OperationsMetricsTestData.BaseUtc.AddHours(10));
        var started = OperationsMetricsTestData.BaseUtc.AddHours(8);
        await service.OpenDowntimeAsync(new OpenDowntimeCommand(
            "concurrent-open",
            "downtime-concurrent",
            "station-a",
            "plc",
            started,
            "agent-a"));

        var first = CaptureAsync(() => service.AttributeDowntimeReasonAsync(
                new AttributeDowntimeReasonCommand(
                    "concurrent-reason-a",
                    "downtime-concurrent",
                    "Mechanical",
                    null,
                    started.AddMinutes(1),
                    "operator-a",
                    ExpectedRevision: 1))
            .AsTask());
        var second = CaptureAsync(() => service.AttributeDowntimeReasonAsync(
                new AttributeDowntimeReasonCommand(
                    "concurrent-reason-b",
                    "downtime-concurrent",
                    "Material",
                    null,
                    started.AddMinutes(1),
                    "operator-b",
                    ExpectedRevision: 1))
            .AsTask());

        var results = await Task.WhenAll(first, second);
        Assert.Single(results, static result => result is null);
        Assert.Single(
            results,
            static result => result is OperationsMetricsConflictException);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
