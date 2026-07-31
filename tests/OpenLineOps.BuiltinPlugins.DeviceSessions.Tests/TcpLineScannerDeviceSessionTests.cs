using OpenLineOps.BuiltinPlugins.DeviceSessions;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions.Tests;

public sealed class TcpLineScannerDeviceSessionTests
{
    [Fact]
    public async Task SubscriptionSurvivesDisconnectWithoutDuplicateOrOutOfOrderSequence()
    {
        await using var server = new LocalScannerServer(
            new ScannerConnectionScript(
                [
                    new ScannerLine("UNIT-001", TimeSpan.FromMilliseconds(10)),
                    new ScannerLine("UNIT-002", TimeSpan.FromMilliseconds(10))
                ]),
            new ScannerConnectionScript(
                [new ScannerLine("UNIT-003", TimeSpan.FromMilliseconds(25))],
                HoldOpen: true));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "scanner-01",
            DeviceSessionTestSupport.ScannerConfiguration(server.Port)));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = plugin.SubscribeAsync(
                new PluginDeviceSignalSubscriptionRequest(
                    session.SessionId,
                    "scanner-subscription",
                    ["scanner.code"]),
                stop.Token)
            .GetAsyncEnumerator(stop.Token);

        var samples = new List<PluginDeviceSignalSample>();
        while (samples.Count < 3 && await subscription.MoveNextAsync())
        {
            samples.Add(subscription.Current.Sample);
        }

        Assert.Equal(["UNIT-001", "UNIT-002", "UNIT-003"],
            samples.Select(static sample => sample.Value.CanonicalValue).ToArray());
        Assert.Equal([1L, 2L, 3L],
            samples.Select(static sample => sample.Sequence).ToArray());
        Assert.All(samples, static sample =>
        {
            Assert.Equal(PluginDeviceSignalQuality.Good, sample.Quality);
            Assert.Equal(TimeSpan.Zero, sample.SourceTimestampUtc.Offset);
            Assert.Equal(TimeSpan.Zero, sample.ObservedTimestampUtc.Offset);
        });
        Assert.True(server.ConnectionCount >= 2);

        await using var resumed = plugin.SubscribeAsync(
                new PluginDeviceSignalSubscriptionRequest(
                    session.SessionId,
                    "scanner-resumed",
                    ["scanner.code"],
                    resumeAfterSequence: 2),
                stop.Token)
            .GetAsyncEnumerator(stop.Token);
        Assert.True(await resumed.MoveNextAsync());
        Assert.Equal(3, resumed.Current.Sequence);
        Assert.Equal("UNIT-003", resumed.Current.Sample.Value.CanonicalValue);
    }

    [Fact]
    public async Task PassiveScannerRejectsWritesAndCachesExactNonIdempotentReplay()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "scanner-write.jsonl");
        await using var server = new LocalScannerServer(
            new ScannerConnectionScript([], HoldOpen: true));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "scanner-02",
            DeviceSessionTestSupport.ScannerConfiguration(server.Port, journalPath)));
        var request = new PluginDeviceSignalWriteRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command(
                "scanner-write",
                60,
                PluginDeviceCommandIdempotencyClass.NonIdempotent),
            [
                new PluginDeviceSignalWrite(
                    "scanner.code",
                    PluginDeviceValue.FromString("SHOULD-NOT-SEND"))
            ]);

        var first = await plugin.WriteAsync(request);
        var replay = await plugin.WriteAsync(request);

        Assert.Equal(PluginDeviceOperationOutcome.Rejected, first.Outcome);
        Assert.Equal(first, replay);
        Assert.Contains("passive", first.FailureReason, StringComparison.OrdinalIgnoreCase);
    }
}
