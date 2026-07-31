namespace OpenLineOps.Operations.Metrics.Domain.Metrics;

public sealed record OeeReport(
    string StationId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int PlannedWindowCount,
    double PlannedProductionSeconds,
    double DowntimeSeconds,
    double OperatingSeconds,
    double PlannedTargetQuantity,
    int CompletedCount,
    int GoodCount,
    int FirstAttemptCount,
    int FirstPassGoodCount,
    double CycleTimeSeconds,
    double TaktSeconds,
    double FirstPassYield,
    double Yield,
    double Availability,
    double Performance,
    double Quality,
    double Oee);

public static class OeeMetricDefinitions
{
    public const string CycleTime =
        "Average declared cycle duration of completed units in the query interval.";

    public const string Takt =
        "Planned production seconds divided by the overlap-prorated planned target.";

    public const string FirstPassYield =
        "Good first-attempt completions divided by all first-attempt completions.";

    public const string Yield =
        "Good completions divided by all completions.";

    public const string Availability =
        "Operating seconds divided by planned production seconds.";

    public const string Performance =
        "Sum of each in-window completion's planned-window ideal cycle time divided by operating seconds, capped at one.";

    public const string Quality =
        "Good completions divided by all completions; identical to Yield for canonical completion facts.";

    public const string Oee =
        "Availability multiplied by Performance multiplied by Quality.";
}
