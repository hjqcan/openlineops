using OpenLineOps.BuiltinPlugins.DeviceSessions;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions.Tests;

public sealed class DurableCommandLedgerTests
{
    [Fact]
    public async Task CompletedNonIdempotentCommandIsReplayedAfterColdRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "completed-ledger.jsonl");
        await using var server = new LocalScpiServer((_, _) => new ScpiServerReply());
        var envelope = new PluginDeviceCommandEnvelope(
            "cold-restart-command",
            100,
            DateTimeOffset.UtcNow.AddMinutes(1),
            PluginDeviceCommandIdempotencyClass.NonIdempotent,
            PluginDeviceCommandSafetyClass.Normal);
        PluginDeviceOperationResult first;
        await using (var firstHost = new IndustrialDeviceSessionPlugin())
        {
            var session = await firstHost.OpenAsync(new PluginDeviceSessionOpenRequest(
                "cold-restart-instrument",
                DeviceSessionTestSupport.ScpiConfiguration(
                    server.Port,
                    journalPath)));
            first = await firstHost.InvokeAsync(new PluginDeviceInvocationRequest(
                session.SessionId,
                envelope,
                "Write",
                """{"command":"OUTPUT ON"}"""));
        }

        await using var restartedHost = new IndustrialDeviceSessionPlugin();
        var restartedSession = await restartedHost.OpenAsync(
            new PluginDeviceSessionOpenRequest(
                "cold-restart-instrument",
                DeviceSessionTestSupport.ScpiConfiguration(
                    server.Port,
                    journalPath)));
        var replay = await restartedHost.InvokeAsync(new PluginDeviceInvocationRequest(
            restartedSession.SessionId,
            envelope,
            "Write",
            """{"command":"OUTPUT ON"}"""));
        var changedEnvelope = await restartedHost.InvokeAsync(
            new PluginDeviceInvocationRequest(
                restartedSession.SessionId,
                new PluginDeviceCommandEnvelope(
                    envelope.CommandId,
                    envelope.FencingToken + 1,
                    envelope.DeadlineUtc,
                    envelope.IdempotencyClass,
                    envelope.SafetyClass),
                "Write",
                """{"command":"OUTPUT ON"}"""));
        await server.WaitForReceivedCountAsync(1);

        Assert.True(first.Succeeded);
        Assert.Equal(first, replay);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, changedEnvelope.Outcome);
        Assert.Contains(
            "different evidence",
            changedEnvelope.FailureReason,
            StringComparison.Ordinal);
        Assert.Equal(["OUTPUT ON"], server.Received.Select(static item => item.Line).ToArray());
    }

    [Fact]
    public async Task DurableRequestWithoutResponseRequiresRecoveryAndIsNeverResent()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "pending-ledger.jsonl");
        await using var server = new LocalScpiServer((_, _) => new ScpiServerReply());
        var envelope = new PluginDeviceCommandEnvelope(
            "interrupted-command",
            200,
            DateTimeOffset.UtcNow.AddMinutes(1),
            PluginDeviceCommandIdempotencyClass.NonIdempotent,
            PluginDeviceCommandSafetyClass.Normal);
        await using (var firstHost = new IndustrialDeviceSessionPlugin())
        {
            var session = await firstHost.OpenAsync(new PluginDeviceSessionOpenRequest(
                "interrupted-instrument",
                DeviceSessionTestSupport.ScpiConfiguration(
                    server.Port,
                    journalPath)));
            var result = await firstHost.InvokeAsync(new PluginDeviceInvocationRequest(
                session.SessionId,
                envelope,
                "Write",
                """{"command":"TRIGGER"}"""));
            Assert.True(result.Succeeded);
            await server.WaitForReceivedCountAsync(1);
        }

        var lines = await File.ReadAllLinesAsync(journalPath);
        Assert.Equal(2, lines.Length);
        await File.WriteAllTextAsync(journalPath, lines[0] + "\n");

        await using var restartedHost = new IndustrialDeviceSessionPlugin();
        var restartedSession = await restartedHost.OpenAsync(
            new PluginDeviceSessionOpenRequest(
                "interrupted-instrument",
                DeviceSessionTestSupport.ScpiConfiguration(
                    server.Port,
                    journalPath)));
        var recovered = await restartedHost.InvokeAsync(new PluginDeviceInvocationRequest(
            restartedSession.SessionId,
            envelope,
            "Write",
            """{"command":"TRIGGER"}"""));
        var stale = await restartedHost.InvokeAsync(new PluginDeviceInvocationRequest(
            restartedSession.SessionId,
            DeviceSessionTestSupport.Command("lower-fence", 199),
            "Write",
            """{"command":"ANOTHER"}"""));
        var diagnostics = await restartedHost.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(restartedSession.SessionId));

        Assert.Equal(PluginDeviceOperationOutcome.Failed, recovered.Outcome);
        Assert.Equal(
            PluginDeviceOperationCompletionState.Unknown,
            recovered.CompletionState);
        Assert.True(recovered.RecoveryRequired);
        Assert.Contains("RecoveryRequired", recovered.FailureReason, StringComparison.Ordinal);
        Assert.Contains("not be resent", recovered.FailureReason, StringComparison.Ordinal);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, stale.Outcome);
        Assert.Contains("stale", stale.FailureReason, StringComparison.Ordinal);
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "DeviceSession.RecoveryRequired");
        Assert.Equal(["TRIGGER"], server.Received.Select(static item => item.Line).ToArray());
    }

    [Fact]
    public async Task NonIdempotentCommandWithoutDurableJournalIsRejectedBeforeSend()
    {
        await using var server = new LocalScpiServer((_, _) => new ScpiServerReply());
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "no-ledger-instrument",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port)));

        var result = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command(
                "unsafe-without-ledger",
                300,
                PluginDeviceCommandIdempotencyClass.NonIdempotent),
            "Write",
            """{"command":"OUTPUT ON"}"""));

        Assert.Equal(PluginDeviceOperationOutcome.Rejected, result.Outcome);
        Assert.Contains("durable", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task ConcurrentExactNonIdempotentReplayHasOnePhysicalSend()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "concurrent-ledger.jsonl");
        await using var server = new LocalScpiServer((_, _) => new ScpiServerReply());
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "concurrent-instrument",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port, journalPath)));
        var request = new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command(
                "concurrent-command",
                350,
                PluginDeviceCommandIdempotencyClass.NonIdempotent),
            "Write",
            """{"command":"PULSE"}""");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => plugin.InvokeAsync(request).AsTask()));
        await server.WaitForReceivedCountAsync(1);

        Assert.All(results, static result => Assert.True(result.Succeeded));
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(["PULSE"], server.Received.Select(static item => item.Line).ToArray());
    }

    [Fact]
    public async Task SameCommandIdWithAnyEnvelopeChangeIsAnEvidenceConflict()
    {
        await using var server = new LocalScpiServer((_, _) => new ScpiServerReply());
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "envelope-instrument",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port)));
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        var baseline = new PluginDeviceCommandEnvelope(
            "envelope-command",
            400,
            deadline,
            PluginDeviceCommandIdempotencyClass.Idempotent,
            PluginDeviceCommandSafetyClass.Normal);
        var first = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            baseline,
            "Write",
            """{"command":"MODE AUTO"}"""));
        var changedEnvelopes = new[]
        {
            new PluginDeviceCommandEnvelope(
                baseline.CommandId,
                401,
                deadline,
                baseline.IdempotencyClass,
                baseline.SafetyClass),
            new PluginDeviceCommandEnvelope(
                baseline.CommandId,
                baseline.FencingToken,
                deadline.AddSeconds(1),
                baseline.IdempotencyClass,
                baseline.SafetyClass),
            new PluginDeviceCommandEnvelope(
                baseline.CommandId,
                baseline.FencingToken,
                deadline,
                PluginDeviceCommandIdempotencyClass.Conditional,
                baseline.SafetyClass),
            new PluginDeviceCommandEnvelope(
                baseline.CommandId,
                baseline.FencingToken,
                deadline,
                baseline.IdempotencyClass,
                PluginDeviceCommandSafetyClass.Motion)
        };

        var conflicts = new List<PluginDeviceOperationResult>();
        foreach (var envelope in changedEnvelopes)
        {
            conflicts.Add(await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
                session.SessionId,
                envelope,
                "Write",
                """{"command":"MODE AUTO"}""")));
        }
        await server.WaitForReceivedCountAsync(1);

        Assert.True(first.Succeeded);
        Assert.All(conflicts, static result =>
        {
            Assert.Equal(PluginDeviceOperationOutcome.Rejected, result.Outcome);
            Assert.Contains(
                "different evidence",
                result.FailureReason,
                StringComparison.Ordinal);
        });
        Assert.Equal(["MODE AUTO"], server.Received.Select(static item => item.Line).ToArray());
    }

    [Fact]
    public async Task InvalidHighFenceDoesNotPoisonValidLowerFence()
    {
        await using var server = new LocalScpiServer((_, _) => new ScpiServerReply());
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "fence-instrument",
            DeviceSessionTestSupport.ScpiConfiguration(server.Port)));
        var safetyCritical = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            new PluginDeviceCommandEnvelope(
                "safety-command",
                10_000,
                DateTimeOffset.UtcNow.AddMinutes(1),
                PluginDeviceCommandIdempotencyClass.Idempotent,
                PluginDeviceCommandSafetyClass.SafetyCritical),
            "Write",
            """{"command":"UNSAFE"}"""));
        var expired = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            new PluginDeviceCommandEnvelope(
                "expired-command",
                20_000,
                DateTimeOffset.UtcNow.AddMilliseconds(-1),
                PluginDeviceCommandIdempotencyClass.Idempotent,
                PluginDeviceCommandSafetyClass.Normal),
            "Write",
            """{"command":"EXPIRED"}"""));
        var valid = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command("valid-command", 10),
            "Write",
            """{"command":"VALID"}"""));
        await server.WaitForReceivedCountAsync(1);

        Assert.Equal(PluginDeviceOperationOutcome.Rejected, safetyCritical.Outcome);
        Assert.Equal(PluginDeviceOperationOutcome.TimedOut, expired.Outcome);
        Assert.True(valid.Succeeded);
        Assert.Equal(["VALID"], server.Received.Select(static item => item.Line).ToArray());
    }
}
