using OpenLineOps.Operations.Metrics.Domain.Downtime;
using OpenLineOps.Operations.Metrics.Domain.Metrics;
using OpenLineOps.Operations.Metrics.Domain.Production;
using OpenLineOps.Operations.Metrics.Domain.Shifts;

namespace OpenLineOps.Operations.Metrics.Application.Metrics;

public static class OeeCalculator
{
    public static OeeReport Calculate(
        string stationId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<PlannedProductionWindow> windows,
        IReadOnlyCollection<CanonicalProductionEvent> productionEvents,
        IReadOnlyCollection<DowntimeInterval> downtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ValidateRange(fromUtc, toUtc);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(productionEvents);
        ArgumentNullException.ThrowIfNull(downtime);

        var effectiveWindows = windows
            .Where(window =>
                string.Equals(window.StationId, stationId, StringComparison.Ordinal)
                && window.StartsAtUtc < toUtc
                && window.EndsAtUtc > fromUtc)
            .Select(window => new EffectiveWindow(
                Max(window.StartsAtUtc, fromUtc),
                Min(window.EndsAtUtc, toUtc),
                window.TargetQuantity
                * (Min(window.EndsAtUtc, toUtc)
                   - Max(window.StartsAtUtc, fromUtc)).TotalSeconds
                / (window.EndsAtUtc - window.StartsAtUtc).TotalSeconds,
                window.IdealCycleTime.TotalSeconds))
            .OrderBy(static window => window.Start)
            .ToArray();

        EnsureWindowsDoNotOverlap(effectiveWindows);
        var plannedSeconds = effectiveWindows.Sum(static window => window.DurationSeconds);
        var plannedTarget = effectiveWindows.Sum(static window => window.ProratedTarget);

        var completionAssignments = productionEvents
            .Where(item =>
                string.Equals(item.StationId, stationId, StringComparison.Ordinal)
                && item.Kind == ProductionEventKind.UnitCompleted
                && item.OccurredAtUtc >= fromUtc
                && item.OccurredAtUtc < toUtc)
            .Select(item => new
            {
                Event = item,
                Window = effectiveWindows.FirstOrDefault(window =>
                    item.OccurredAtUtc >= window.Start
                    && item.OccurredAtUtc < window.End)
            })
            .Where(static item => item.Window is not null)
            .ToArray();
        var completions = completionAssignments
            .Select(static item => item.Event)
            .ToArray();
        var completedCount = completions.Length;
        var goodCount = completions.Count(static item => item.Good);
        var firstAttempts = completions.Where(static item => item.FirstAttempt).ToArray();
        var firstPassGood = firstAttempts.Count(static item => item.Good);
        var cycleTime = completedCount == 0
            ? 0
            : completions.Average(static item => item.CycleDuration!.Value.TotalSeconds);
        var takt = plannedTarget <= 0 ? 0 : plannedSeconds / plannedTarget;

        var clippedDowntime = downtime
            .Where(item => string.Equals(
                item.StationId,
                stationId,
                StringComparison.Ordinal))
            .SelectMany(item => effectiveWindows.Select(window =>
                Intersect(
                    item.StartedAtUtc,
                    item.SourceClearedAtUtc ?? toUtc,
                    window.Start,
                    window.End)))
            .Where(static interval => interval is not null)
            .Select(static interval => interval!.Value)
            .OrderBy(static interval => interval.Start)
            .ToArray();
        var downtimeSeconds = UnionDurationSeconds(clippedDowntime);
        var operatingSeconds = Math.Max(0, plannedSeconds - downtimeSeconds);

        var firstPassYield = Divide(firstPassGood, firstAttempts.Length);
        var yield = Divide(goodCount, completedCount);
        var availability = Divide(operatingSeconds, plannedSeconds);
        var idealProductionSeconds = completionAssignments.Sum(
            static item => item.Window!.IdealCycleTimeSeconds);
        var performance = operatingSeconds <= 0
            ? 0
            : Math.Min(1, idealProductionSeconds / operatingSeconds);
        var quality = yield;
        var oee = availability * performance * quality;

        return new OeeReport(
            stationId,
            fromUtc,
            toUtc,
            effectiveWindows.Length,
            plannedSeconds,
            downtimeSeconds,
            operatingSeconds,
            plannedTarget,
            completedCount,
            goodCount,
            firstAttempts.Length,
            firstPassGood,
            cycleTime,
            takt,
            firstPassYield,
            yield,
            availability,
            performance,
            quality,
            oee);
    }

    private static void ValidateRange(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        if (fromUtc == default
            || toUtc == default
            || fromUtc.Offset != TimeSpan.Zero
            || toUtc.Offset != TimeSpan.Zero
            || toUtc <= fromUtc)
        {
            throw new ArgumentException(
                "The metrics query requires a non-empty UTC half-open interval.");
        }
    }

    private static void EnsureWindowsDoNotOverlap(
        IReadOnlyList<EffectiveWindow> windows)
    {
        for (var index = 1; index < windows.Count; index++)
        {
            if (windows[index].Start < windows[index - 1].End)
            {
                throw new InvalidDataException(
                    "Persisted planned production windows overlap.");
            }
        }
    }

    private static TimeInterval? Intersect(
        DateTimeOffset leftStart,
        DateTimeOffset leftEnd,
        DateTimeOffset rightStart,
        DateTimeOffset rightEnd)
    {
        var start = Max(leftStart, rightStart);
        var end = Min(leftEnd, rightEnd);
        return end <= start ? null : new TimeInterval(start, end);
    }

    private static double UnionDurationSeconds(
        IReadOnlyList<TimeInterval> intervals)
    {
        if (intervals.Count == 0)
        {
            return 0;
        }

        var total = 0d;
        var currentStart = intervals[0].Start;
        var currentEnd = intervals[0].End;
        foreach (var interval in intervals.Skip(1))
        {
            if (interval.Start <= currentEnd)
            {
                currentEnd = Max(currentEnd, interval.End);
                continue;
            }

            total += (currentEnd - currentStart).TotalSeconds;
            currentStart = interval.Start;
            currentEnd = interval.End;
        }

        return total + (currentEnd - currentStart).TotalSeconds;
    }

    private static double Divide(double numerator, double denominator) =>
        denominator <= 0 ? 0 : numerator / denominator;

    private static DateTimeOffset Max(
        DateTimeOffset left,
        DateTimeOffset right) => left >= right ? left : right;

    private static DateTimeOffset Min(
        DateTimeOffset left,
        DateTimeOffset right) => left <= right ? left : right;

    private sealed record EffectiveWindow(
        DateTimeOffset Start,
        DateTimeOffset End,
        double ProratedTarget,
        double IdealCycleTimeSeconds)
    {
        public double DurationSeconds => (End - Start).TotalSeconds;
    }

    private readonly record struct TimeInterval(
        DateTimeOffset Start,
        DateTimeOffset End);
}
