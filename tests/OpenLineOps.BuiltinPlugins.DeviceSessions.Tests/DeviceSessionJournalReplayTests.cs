using System.Diagnostics;
using OpenLineOps.BuiltinPlugins.DeviceSessions;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions.Tests;

public sealed class DeviceSessionJournalReplayTests
{
    [Fact]
    public async Task JournalHashChainAndRecordedCommandCanBeReplayedIdempotently()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "scpi-session.jsonl");
        await using (var server = new LocalScpiServer((_, _) =>
                         new ScpiServerReply("42")))
        await using (var recorder = new IndustrialDeviceSessionPlugin())
        {
            var session = await recorder.OpenAsync(new PluginDeviceSessionOpenRequest(
                "recorded-instrument",
                DeviceSessionTestSupport.ScpiConfiguration(
                    server.Port,
                    journalPath)));
            var recorded = await recorder.InvokeAsync(new PluginDeviceInvocationRequest(
                session.SessionId,
                DeviceSessionTestSupport.Command("recorded-query", 70),
                "Query",
                """{"command":"MEAS?"}"""));
            Assert.True(recorded.Succeeded);
        }

        var entries = await DeviceSessionJournal.ReadAndVerifyAsync(journalPath);
        Assert.Equal(2, entries.Count);
        Assert.Equal("command.request", entries[0].Kind);
        Assert.Equal("command.response", entries[1].Kind);
        Assert.Null(entries[0].PreviousSha256);
        Assert.NotNull(entries[1].PreviousSha256);

        await using var replay = new IndustrialDeviceSessionPlugin();
        var replaySession = await replay.OpenAsync(new PluginDeviceSessionOpenRequest(
            "replay-instrument",
            DeviceSessionTestSupport.ReplayConfiguration(journalPath)));
        var request = new PluginDeviceInvocationRequest(
            replaySession.SessionId,
            DeviceSessionTestSupport.Command("replay-query", 80),
            "Query",
            """{"command":"MEAS?"}""");
        var first = await replay.InvokeAsync(request);
        var duplicate = await replay.InvokeAsync(request);

        Assert.True(first.Succeeded);
        Assert.Equal(first, duplicate);
        Assert.Contains("42", first.OutputPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignalReplayPreservesTimingAndSequenceAndInjectsFaults()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "scanner-session.jsonl");
        await using (var server = new LocalScannerServer(
                         new ScannerConnectionScript(
                             [
                                 new ScannerLine("A", TimeSpan.FromMilliseconds(10)),
                                 new ScannerLine("B", TimeSpan.FromMilliseconds(120))
                             ],
                             HoldOpen: true)))
        await using (var recorder = new IndustrialDeviceSessionPlugin())
        {
            var session = await recorder.OpenAsync(new PluginDeviceSessionOpenRequest(
                "recorded-scanner",
                DeviceSessionTestSupport.ScannerConfiguration(
                    server.Port,
                    journalPath)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var subscription = recorder.SubscribeAsync(
                    new PluginDeviceSignalSubscriptionRequest(
                        session.SessionId,
                        "recording-subscription",
                        ["scanner.code"]),
                    stop.Token)
                .GetAsyncEnumerator(stop.Token);
            Assert.True(await subscription.MoveNextAsync());
            Assert.True(await subscription.MoveNextAsync());
        }

        var entries = await DeviceSessionJournal.ReadAndVerifyAsync(journalPath);
        Assert.Equal(2, entries.Count);
        var disconnectSequence = entries[1].JournalSequence;
        await using var replay = new IndustrialDeviceSessionPlugin();
        var sessionReplay = await replay.OpenAsync(new PluginDeviceSessionOpenRequest(
            "replay-scanner",
            DeviceSessionTestSupport.ReplayConfiguration(
                journalPath,
                speedFactor: 1,
                disconnectAtJournalSequence: disconnectSequence,
                badQualitySignalIds: ["scanner.code"])));
        using var replayStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var replaySubscription = replay.SubscribeAsync(
                new PluginDeviceSignalSubscriptionRequest(
                    sessionReplay.SessionId,
                    "replay-subscription",
                    ["scanner.code"]),
                replayStop.Token)
            .GetAsyncEnumerator(replayStop.Token);

        Assert.True(await replaySubscription.MoveNextAsync());
        var first = replaySubscription.Current.Sample;
        var stopwatch = Stopwatch.StartNew();
        Assert.True(await replaySubscription.MoveNextAsync());
        stopwatch.Stop();
        var second = replaySubscription.Current.Sample;
        var diagnostics = await replay.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(sessionReplay.SessionId));

        Assert.Equal([1L, 2L], new[] { first.Sequence, second.Sequence });
        Assert.Equal(["A", "B"],
            new[] { first.Value.CanonicalValue, second.Value.CanonicalValue });
        Assert.All(
            new[] { first, second },
            static sample => Assert.Equal(
                PluginDeviceSignalQuality.Bad,
                sample.Quality));
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromMilliseconds(75),
            $"Expected replay timing delay, observed {stopwatch.Elapsed}.");
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "DeviceReplay.InjectedDisconnect");
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "DeviceReplay.Reconnected");
    }

    [Fact]
    public async Task RecordedSignalSequenceContinuesAcrossColdRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "continued-signals.jsonl");
        await using var server = new LocalScpiServer((_, _) =>
            new ScpiServerReply("7"));
        PluginDeviceSignalSample first;
        await using (var firstHost = new IndustrialDeviceSessionPlugin())
        {
            var session = await firstHost.OpenAsync(new PluginDeviceSessionOpenRequest(
                "continued-signal-instrument",
                DeviceSessionTestSupport.ScpiConfiguration(
                    server.Port,
                    journalPath)));
            first = Assert.Single(await firstHost.ReadAsync(
                new PluginDeviceSignalReadRequest(
                    session.SessionId,
                    ["MEAS?"])));
        }

        PluginDeviceSignalSample second;
        await using (var restartedHost = new IndustrialDeviceSessionPlugin())
        {
            var session = await restartedHost.OpenAsync(new PluginDeviceSessionOpenRequest(
                "continued-signal-instrument",
                DeviceSessionTestSupport.ScpiConfiguration(
                    server.Port,
                    journalPath)));
            second = Assert.Single(await restartedHost.ReadAsync(
                new PluginDeviceSignalReadRequest(
                    session.SessionId,
                    ["MEAS?"])));
        }

        var entries = await DeviceSessionJournal.ReadAndVerifyAsync(journalPath);
        Assert.Equal([1L, 2L], new[] { first.Sequence, second.Sequence });
        Assert.Equal(
            [1L, 2L],
            entries.Select(static entry => entry.DeviceSequence!.Value).ToArray());

        await using var replay = new IndustrialDeviceSessionPlugin();
        var replaySession = await replay.OpenAsync(new PluginDeviceSessionOpenRequest(
            "continued-signal-replay",
            DeviceSessionTestSupport.ReplayConfiguration(
                journalPath,
                speedFactor: 10_000)));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = replay.SubscribeAsync(
                new PluginDeviceSignalSubscriptionRequest(
                    replaySession.SessionId,
                    "continued-signals",
                    ["MEAS?"]),
                stop.Token)
            .GetAsyncEnumerator(stop.Token);
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(1, subscription.Current.Sequence);
        Assert.True(await subscription.MoveNextAsync());
        Assert.Equal(2, subscription.Current.Sequence);
    }

    [Fact]
    public async Task TamperedJournalIsRejectedBeforeReplayOpens()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "tampered.jsonl");
        await using (var server = new LocalScpiServer((_, _) =>
                         new ScpiServerReply("OK")))
        await using (var recorder = new IndustrialDeviceSessionPlugin())
        {
            var session = await recorder.OpenAsync(new PluginDeviceSessionOpenRequest(
                "tamper-source",
                DeviceSessionTestSupport.ScpiConfiguration(
                    server.Port,
                    journalPath)));
            await recorder.InvokeAsync(new PluginDeviceInvocationRequest(
                session.SessionId,
                DeviceSessionTestSupport.Command("tamper-query", 90),
                "Query",
                """{"command":"PING?"}"""));
        }

        var original = await File.ReadAllTextAsync(journalPath);
        var hashMarker = "\"sha256\":\"";
        var hashStart = original.IndexOf(hashMarker, StringComparison.Ordinal)
                        + hashMarker.Length;
        Assert.True(hashStart >= hashMarker.Length);
        var replacement = original[hashStart] == '0' ? '1' : '0';
        var tampered = original[..hashStart]
                       + replacement
                       + original[(hashStart + 1)..];
        Assert.NotEqual(original, tampered);
        await File.WriteAllTextAsync(journalPath, tampered);
        await using var replay = new IndustrialDeviceSessionPlugin();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await replay.OpenAsync(new PluginDeviceSessionOpenRequest(
                "tampered-replay",
                DeviceSessionTestSupport.ReplayConfiguration(journalPath))));
    }
}
