using OpenLineOps.BuiltinPlugins.DeviceSessions;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions.Tests;

public sealed class ScpiTcpDeviceSessionTests
{
    [Fact]
    public async Task QueryReadAndWriteUseOneSerializedLongLivedConnection()
    {
        await using var server = new LocalScpiServer((_, line) => line switch
        {
            "*IDN?" => new ScpiServerReply("OpenLineOps,Fixture,1,1"),
            "MEAS:VOLT?" => new ScpiServerReply("12.5"),
            _ => new ScpiServerReply()
        });
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "instrument-01",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port)));

        var query = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command("query-01", 10),
            "Query",
            """{"command":"*IDN?"}"""));
        var read = Assert.Single(await plugin.ReadAsync(
            new PluginDeviceSignalReadRequest(
                session.SessionId,
                ["MEAS:VOLT?"])));
        var write = await plugin.WriteAsync(new PluginDeviceSignalWriteRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command("write-01", 10),
            [
                new PluginDeviceSignalWrite(
                    "CONF:MODE",
                    PluginDeviceValue.FromString("AUTO"))
            ]));
        await server.WaitForReceivedCountAsync(3);

        Assert.True(query.Succeeded);
        Assert.Contains("OpenLineOps,Fixture,1,1", query.OutputPayload, StringComparison.Ordinal);
        Assert.Equal("12.5", read.Value.CanonicalValue);
        Assert.Equal(PluginDeviceSignalQuality.Good, read.Quality);
        Assert.Equal(1, read.Sequence);
        Assert.True(write.Succeeded);
        Assert.Equal(
            ["*IDN?", "MEAS:VOLT?", "CONF:MODE AUTO"],
            server.Received.Select(static item => item.Line).ToArray());
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task IdempotentQueryReconnectsAfterAmbiguousDisconnect()
    {
        await using var server = new LocalScpiServer((connection, _) =>
            connection == 1
                ? new ScpiServerReply(CloseWithoutResponse: true)
                : new ScpiServerReply("READY"));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "instrument-02",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port)));

        var result = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command("query-reconnect", 20),
            "Query",
            """{"command":"STATUS?"}"""));
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));
        var diagnostics = await plugin.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.True(result.Succeeded);
        Assert.Contains("READY", result.OutputPayload, StringComparison.Ordinal);
        Assert.Equal(2, server.Received.Count);
        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(PluginDeviceHealthStatus.Healthy, health.Status);
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "ScpiTcp.Disconnected");
    }

    [Fact]
    public async Task NonIdempotentReplayNeverSendsTwiceAndConflictIsRejected()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "non-idempotent.jsonl");
        await using var server = new LocalScpiServer((_, _) => new ScpiServerReply());
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "instrument-03",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port, journalPath)));
        var request = new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command(
                "physical-write-01",
                30,
                PluginDeviceCommandIdempotencyClass.NonIdempotent),
            "Write",
            """{"command":"OUTP ON"}""");

        var first = await plugin.InvokeAsync(request);
        var replay = await plugin.InvokeAsync(request);
        var conflict = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            request.Command,
            "Write",
            """{"command":"OUTP OFF"}"""));
        var staleFence = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command("stale-write", 29),
            "Write",
            """{"command":"OUTP OFF"}"""));
        await server.WaitForReceivedCountAsync(1);

        Assert.True(first.Succeeded);
        Assert.Equal(first, replay);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, conflict.Outcome);
        Assert.Contains("different evidence", conflict.FailureReason, StringComparison.Ordinal);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, staleFence.Outcome);
        Assert.Contains("stale", staleFence.FailureReason, StringComparison.Ordinal);
        Assert.Equal(["OUTP ON"], server.Received.Select(static item => item.Line).ToArray());
    }

    [Fact]
    public async Task NonIdempotentQueryIsNotResentAfterTransmissionDisconnect()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "ambiguous-query.jsonl");
        await using var server = new LocalScpiServer((_, _) =>
            new ScpiServerReply(CloseWithoutResponse: true));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "instrument-04",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port, journalPath)));
        var request = new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command(
                "non-idempotent-query",
                40,
                PluginDeviceCommandIdempotencyClass.NonIdempotent),
            "Query",
            """{"command":"TRIG;READ?"}""");

        var result = await plugin.InvokeAsync(request);
        var replay = await plugin.InvokeAsync(request);

        Assert.Equal(PluginDeviceOperationOutcome.Failed, result.Outcome);
        Assert.Equal(result, replay);
        Assert.Single(server.Received);
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task DeadlineAndCallerCancellationBoundBlockedQuery()
    {
        await using var server = new LocalScpiServer((_, _) =>
            new ScpiServerReply("LATE", Delay: TimeSpan.FromSeconds(2)));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "instrument-05",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port)));

        var timedOut = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command(
                "timeout-query",
                50,
                deadline: TimeSpan.FromMilliseconds(50)),
            "Query",
            """{"command":"SLOW?"}"""));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancelled = await plugin.InvokeAsync(
            new PluginDeviceInvocationRequest(
                session.SessionId,
                DeviceSessionTestSupport.Command("cancel-query", 50),
                "Query",
                """{"command":"SLOW?"}"""),
            cancel.Token);

        Assert.Equal(PluginDeviceOperationOutcome.TimedOut, timedOut.Outcome);
        Assert.Equal(PluginDeviceOperationOutcome.Failed, cancelled.Outcome);
        Assert.Contains("cancellation", cancelled.FailureReason, StringComparison.OrdinalIgnoreCase);
    }
}
