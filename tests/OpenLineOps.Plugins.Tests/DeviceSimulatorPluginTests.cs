using OpenLineOps.Plugin.Abstractions;
using OpenLineOps.SamplePlugins.DeviceSimulator;

namespace OpenLineOps.Plugins.Tests;

public sealed class DeviceSimulatorPluginTests
{
    [Fact]
    public async Task TypedWriteReadAndSubscriptionUseStableSequenceAndUnits()
    {
        await using var plugin = new DeviceSimulatorPlugin();
        var session = await plugin.OpenAsync(
            new PluginDeviceSessionOpenRequest("simulator-01"));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscription = plugin.SubscribeAsync(
                new PluginDeviceSignalSubscriptionRequest(
                    session.SessionId,
                    "subscription-01",
                    ["sim.measurement"]),
                stop.Token)
            .GetAsyncEnumerator(stop.Token);
        var write = plugin.WriteAsync(
            new PluginDeviceSignalWriteRequest(
                session.SessionId,
                Command("write-01", 11),
                [
                    new PluginDeviceSignalWrite(
                        "sim.measurement",
                        PluginDeviceValue.FromDouble(12.5),
                        "V")
                ]),
            stop.Token);

        Assert.True((await write).Succeeded);
        Assert.True(await subscription.MoveNextAsync());
        var sample = subscription.Current.Sample;
        var read = Assert.Single(await plugin.ReadAsync(
            new PluginDeviceSignalReadRequest(
                session.SessionId,
                ["sim.measurement"]),
            stop.Token));

        Assert.Equal("12.5", sample.Value.CanonicalValue);
        Assert.Equal("V", sample.Unit);
        Assert.Equal(PluginDeviceSignalQuality.Good, sample.Quality);
        Assert.Equal(sample.Sequence, read.Sequence);
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task ExactNonIdempotentReplayDoesNotApplyIncrementTwice()
    {
        await using var plugin = new DeviceSimulatorPlugin();
        var session = await plugin.OpenAsync(
            new PluginDeviceSessionOpenRequest("simulator-02"));
        var request = new PluginDeviceInvocationRequest(
            session.SessionId,
            Command(
                "increment-01",
                21,
                PluginDeviceCommandIdempotencyClass.NonIdempotent),
            "Increment",
            """{"signalId":"sim.counter","delta":1}""");

        var first = await plugin.InvokeAsync(request);
        var replay = await plugin.InvokeAsync(request);
        var signal = Assert.Single(await plugin.ReadAsync(
            new PluginDeviceSignalReadRequest(session.SessionId, ["sim.counter"])));

        Assert.True(first.Succeeded);
        Assert.Equal(first, replay);
        Assert.Equal("1", signal.Value.CanonicalValue);
        Assert.Equal(1, signal.Sequence);
    }

    [Fact]
    public async Task ConflictingCommandIdentityAndStaleFenceAreRejected()
    {
        await using var plugin = new DeviceSimulatorPlugin();
        var session = await plugin.OpenAsync(
            new PluginDeviceSessionOpenRequest("simulator-03"));
        var first = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("increment-02", 31),
            "Increment",
            """{"signalId":"sim.counter","delta":1}"""));
        var conflict = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("increment-02", 31),
            "Increment",
            """{"signalId":"sim.counter","delta":2}"""));
        var stale = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("increment-03", 30),
            "Increment",
            """{"signalId":"sim.counter","delta":1}"""));

        Assert.True(first.Succeeded);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, conflict.Outcome);
        Assert.Contains("different evidence", conflict.FailureReason, StringComparison.Ordinal);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, stale.Outcome);
        Assert.Contains("stale", stale.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayAndFaultInjectionAreVisibleInSignalsHealthAndDiagnostics()
    {
        await using var plugin = new DeviceSimulatorPlugin();
        var session = await plugin.OpenAsync(
            new PluginDeviceSessionOpenRequest("simulator-04"));
        var replay = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("replay-01", 41),
            "Replay",
            """
            {
              "samples": [
                {
                  "signalId": "sim.measurement",
                  "type": "floatingPoint",
                  "canonicalValue": "3.25",
                  "unit": "V"
                }
              ]
            }
            """));
        var injected = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("fault-01", 42),
            "InjectFault",
            """{"reason":"Injected cable disconnect."}"""));
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));
        var sample = Assert.Single(await plugin.ReadAsync(
            new PluginDeviceSignalReadRequest(
                session.SessionId,
                ["sim.measurement"])));
        var diagnostics = await plugin.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.True(replay.Succeeded);
        Assert.True(injected.Succeeded);
        Assert.Equal(PluginDeviceHealthStatus.Unhealthy, health.Status);
        Assert.Equal(PluginDeviceSignalQuality.Bad, sample.Quality);
        Assert.Equal("3.25", sample.Value.CanonicalValue);
        Assert.Contains(
            diagnostics.Entries,
            entry => entry.Code == "Simulator.InjectedFault");
    }

    [Fact]
    public async Task ConfiguredFaultAfterOperationFailsDeterministically()
    {
        await using var plugin = new DeviceSimulatorPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "simulator-05",
            """
            {
              "heartbeatMilliseconds": 1000,
              "operationLatencyMilliseconds": 0,
              "failAfterOperations": 1,
              "signals": [
                {
                  "signalId": "counter",
                  "type": "signedInteger",
                  "canonicalValue": "0",
                  "unit": "count"
                }
              ]
            }
            """));

        var first = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("configured-01", 51),
            "Increment",
            """{"signalId":"counter","delta":1}"""));
        var second = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("configured-02", 51),
            "Increment",
            """{"signalId":"counter","delta":1}"""));

        Assert.True(first.Succeeded);
        Assert.Equal(PluginDeviceOperationOutcome.Failed, second.Outcome);
        Assert.Contains("Configured fault", second.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentExactReplayWaitsForOnePhysicalExecution()
    {
        await using var plugin = new DeviceSimulatorPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "simulator-06",
            """
            {
              "heartbeatMilliseconds": 1000,
              "operationLatencyMilliseconds": 50,
              "failAfterOperations": null,
              "signals": [
                {
                  "signalId": "counter",
                  "type": "signedInteger",
                  "canonicalValue": "0",
                  "unit": "count"
                }
              ]
            }
            """));
        var request = new PluginDeviceInvocationRequest(
            session.SessionId,
            Command(
                "concurrent-increment-01",
                61,
                PluginDeviceCommandIdempotencyClass.NonIdempotent),
            "Increment",
            """{"signalId":"counter","delta":1}""");

        var first = plugin.InvokeAsync(request).AsTask();
        var replay = plugin.InvokeAsync(request).AsTask();
        var results = await Task.WhenAll(first, replay);
        var signal = Assert.Single(await plugin.ReadAsync(
            new PluginDeviceSignalReadRequest(session.SessionId, ["counter"])));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(results[0], results[1]);
        Assert.Equal("1", signal.Value.CanonicalValue);
        Assert.Equal(1, signal.Sequence);
    }

    [Fact]
    public async Task FaultClearCommandRemainsAvailableWhileSessionIsFaulted()
    {
        await using var plugin = new DeviceSimulatorPlugin();
        var session = await plugin.OpenAsync(
            new PluginDeviceSessionOpenRequest("simulator-07"));

        var injected = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("fault-02", 71),
            "InjectFault",
            """{"reason":"Injected device fault."}"""));
        var rejected = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("increment-04", 71),
            "Increment",
            """{"signalId":"sim.counter","delta":1}"""));
        var cleared = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("clear-fault-01", 71),
            "ClearFault"));
        var resumed = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            Command("increment-05", 71),
            "Increment",
            """{"signalId":"sim.counter","delta":1}"""));
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.True(injected.Succeeded);
        Assert.Equal(PluginDeviceOperationOutcome.Failed, rejected.Outcome);
        Assert.True(cleared.Succeeded);
        Assert.True(resumed.Succeeded);
        Assert.Equal(PluginDeviceHealthStatus.Healthy, health.Status);
    }

    [Fact]
    public async Task TenThousandNonIdempotentActionBoundariesHaveZeroDuplicateExecution()
    {
        const int cycleCount = 10_000;
        await using var plugin = new DeviceSimulatorPlugin();
        var session = await plugin.OpenAsync(
            new PluginDeviceSessionOpenRequest("simulator-soak"));

        for (var cycle = 1; cycle <= cycleCount; cycle++)
        {
            var request = new PluginDeviceInvocationRequest(
                session.SessionId,
                Command(
                    $"soak-{cycle:D5}",
                    81,
                    PluginDeviceCommandIdempotencyClass.NonIdempotent),
                "Increment",
                """{"signalId":"sim.counter","delta":1}""");

            var first = await plugin.InvokeAsync(request);
            var duplicate = await plugin.InvokeAsync(request);

            Assert.True(first.Succeeded);
            Assert.Equal(first, duplicate);
        }

        var counter = Assert.Single(await plugin.ReadAsync(
            new PluginDeviceSignalReadRequest(
                session.SessionId,
                ["sim.counter"])));

        Assert.Equal(cycleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            counter.Value.CanonicalValue);
        Assert.Equal(cycleCount, counter.Sequence);
    }

    private static PluginDeviceCommandEnvelope Command(
        string commandId,
        long fencingToken,
        PluginDeviceCommandIdempotencyClass idempotencyClass =
            PluginDeviceCommandIdempotencyClass.Idempotent) =>
        new(
            commandId,
            fencingToken,
            DateTimeOffset.UtcNow.AddMinutes(1),
            idempotencyClass,
            PluginDeviceCommandSafetyClass.Normal);
}
