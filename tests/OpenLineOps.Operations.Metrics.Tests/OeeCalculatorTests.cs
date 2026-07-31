using OpenLineOps.Operations.Metrics.Application.Metrics;
using OpenLineOps.Operations.Metrics.Domain.Downtime;
using OpenLineOps.Operations.Metrics.Domain.Shifts;

namespace OpenLineOps.Operations.Metrics.Tests;

public sealed class OeeCalculatorTests
{
    [Fact]
    public void CrossShiftQueryProratesTargetsAndUsesCanonicalDefinitions()
    {
        var day = OperationsMetricsTestData.BaseUtc;
        var windows = new[]
        {
            Window("window-1", "shift-1", day.AddHours(8), day.AddHours(9)),
            Window("window-2", "shift-2", day.AddHours(9), day.AddHours(10))
        };
        var opened = DowntimeInterval.Open(
            "down-open",
            "down-1",
            "station-a",
            "plc",
            day.AddHours(8).AddMinutes(45),
            "agent");
        var clearedFact = opened.ClearFromSource(
            "down-clear",
            "station-a",
            "plc",
            day.AddHours(9).AddMinutes(15),
            "agent");
        var downtime = DowntimeInterval.Rehydrate(
            [opened.Facts[0], clearedFact]);
        var events = Enumerable.Range(0, 30)
            .Select(index => OperationsMetricsTestData.Completed(
                $"event-{index:D2}",
                day.AddHours(8).AddMinutes(31 + (index * 2)),
                firstAttempt: index < 20,
                good: index < 18 || (index >= 20 && index < 26),
                cycleSeconds: 50))
            .Append(OperationsMetricsTestData.Completed(
                "event-outside-window",
                day.AddHours(8).AddMinutes(20),
                firstAttempt: true,
                good: false,
                cycleSeconds: 500))
            .Reverse()
            .ToArray();

        var report = OeeCalculator.Calculate(
            "station-a",
            day.AddHours(8).AddMinutes(30),
            day.AddHours(9).AddMinutes(30),
            windows,
            events,
            [downtime]);

        Assert.Equal(2, report.PlannedWindowCount);
        Assert.Equal(3600, report.PlannedProductionSeconds, precision: 6);
        Assert.Equal(60, report.PlannedTargetQuantity, precision: 6);
        Assert.Equal(1800, report.DowntimeSeconds, precision: 6);
        Assert.Equal(60, report.TaktSeconds, precision: 6);
        Assert.Equal(50, report.CycleTimeSeconds, precision: 6);
        Assert.Equal(0.9, report.FirstPassYield, precision: 6);
        Assert.Equal(0.8, report.Yield, precision: 6);
        Assert.Equal(0.5, report.Availability, precision: 6);
        Assert.Equal(0.416667, report.Performance, precision: 6);
        Assert.Equal(0.166667, report.Oee, precision: 6);
    }

    [Fact]
    public void EmptyDenominatorsProduceFiniteZeros()
    {
        var from = OperationsMetricsTestData.BaseUtc;
        var report = OeeCalculator.Calculate(
            "station-a",
            from,
            from.AddHours(1),
            [],
            [],
            []);

        Assert.Equal(0, report.CycleTimeSeconds);
        Assert.Equal(0, report.TaktSeconds);
        Assert.Equal(0, report.FirstPassYield);
        Assert.Equal(0, report.Yield);
        Assert.Equal(0, report.Availability);
        Assert.Equal(0, report.Performance);
        Assert.Equal(0, report.Quality);
        Assert.Equal(0, report.Oee);
    }

    private static PlannedProductionWindow Window(
        string windowId,
        string shiftId,
        DateTimeOffset start,
        DateTimeOffset end) =>
        new(
            windowId,
            shiftId,
            "station-a",
            start,
            end,
            targetQuantity: 60,
            idealCycleTime: shiftId == "shift-1"
                ? TimeSpan.FromSeconds(30)
                : TimeSpan.FromSeconds(20),
            "engineer",
            OperationsMetricsTestData.BaseUtc,
            PlannedProductionWindow.CurrentSchemaVersion);
}
