namespace OpenLineOps.Runtime.Application.Stations;

public sealed class StationControllerHandshakeOptions
{
    public const string SectionName =
        "OpenLineOps:Runtime:StationControllerHandshake";

    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaximumSourceClockSkew { get; init; } =
        TimeSpan.FromSeconds(30);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (TimeToLive < TimeSpan.FromMilliseconds(100)
            || TimeToLive > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeToLive must be between 00:00:00.100 and 00:05:00.");
        }

        if (MaximumSourceClockSkew < TimeSpan.Zero
            || MaximumSourceClockSkew > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"{SectionName}:MaximumSourceClockSkew must be between 00:00:00 "
                + "and 00:05:00.");
        }

        if (CommandTimeout < TimeSpan.FromSeconds(1)
            || CommandTimeout > TimeSpan.FromMinutes(10))
        {
            throw new InvalidOperationException(
                $"{SectionName}:CommandTimeout must be between 00:00:01 and 00:10:00.");
        }
    }
}

public sealed record StationControllerHandshakeReadiness(
    bool Allowed,
    string Code,
    string Reason);

public static class StationControllerHandshakeReadinessEvaluator
{
    public static StationControllerHandshakeReadiness Evaluate(
        OpenLineOps.Runtime.Domain.Stations.StationControllerHandshake state,
        DateTimeOffset nowUtc,
        StationControllerHandshakeOptions options,
        bool requireRecipeConfirmation = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (nowUtc == default || nowUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Current time must be a non-default UTC timestamp.",
                nameof(nowUtc));
        }

        var report = state.LatestReport;
        if (nowUtc < report.ReceivedAtUtc)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeServerClockInvalid",
                "Current server time precedes the last received controller heartbeat.");
        }

        if (nowUtc - report.ReceivedAtUtc > options.TimeToLive)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeStale",
                $"Controller heartbeat expired at "
                + $"{report.ReceivedAtUtc.Add(options.TimeToLive):O}.");
        }

        if ((report.ReceivedAtUtc - report.SourceTimestampUtc).Duration()
            > options.MaximumSourceClockSkew)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeClockSkew",
                "Controller source time exceeds the configured clock-skew boundary.");
        }

        if (state.RecoveryRequired)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeRecoveryRequired",
                "Controller session changed and requires an authorized recovery acknowledgement.");
        }

        if (report.Error)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeError",
                $"Controller reports error {report.ErrorCode}.");
        }

        if (report.Busy)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeBusy",
                "Controller is still busy with a prior command.");
        }

        if (report.CommandSequence != report.AcknowledgedCommandSequence)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeCommandUnacknowledged",
                $"Controller acknowledged command sequence "
                + $"{report.AcknowledgedCommandSequence}, expected {report.CommandSequence}.");
        }

        if (requireRecipeConfirmation && !report.RecipeConfirmed)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeRecipeUnconfirmed",
                "Controller has not confirmed the released recipe.");
        }

        if (!report.SafetyPermitGranted)
        {
            return Rejected(
                "Runtime.StationControllerHandshakeSafetyPermitDenied",
                "Safety controller permit is denied.");
        }

        return new StationControllerHandshakeReadiness(
            true,
            string.Empty,
            "Controller handshake permits station execution.");
    }

    private static StationControllerHandshakeReadiness Rejected(
        string code,
        string reason) =>
        new(false, code, reason);
}
