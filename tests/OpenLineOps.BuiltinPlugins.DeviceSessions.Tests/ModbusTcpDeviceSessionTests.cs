using OpenLineOps.BuiltinPlugins.DeviceSessions;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions.Tests;

public sealed class ModbusTcpDeviceSessionTests
{
    [Fact]
    public async Task ReadsAllFourDataAreasAcrossFragmentedFrames()
    {
        await using var server = new LocalModbusTcpServer((_, request) =>
        {
            var responsePdu = request.Pdu[0] switch
            {
                0x01 => new byte[] { 0x01, 0x01, 0x01 },
                0x02 => [0x02, 0x01, 0x00],
                0x03 => [0x03, 0x02, 0x12, 0x34],
                0x04 => [0x04, 0x02, 0xFF, 0xFE],
                _ => throw new InvalidOperationException("Unexpected function.")
            };
            return new ModbusServerReply(
                ModbusTestFrames.Response(request, responsePdu),
                FragmentSizes: Enumerable.Repeat(1, 16).ToArray());
        });
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-read-device",
            DeviceSessionTestSupport.ModbusConfiguration(server.Port)));

        var samples = (await plugin.ReadAsync(new PluginDeviceSignalReadRequest(
                session.SessionId,
                [
                    "line.ready",
                    "line.blocked",
                    "line.speed",
                    "line.temperature"
                ])))
            .ToArray();

        Assert.Collection(
            samples,
            sample => AssertSample(
                sample,
                "line.ready",
                PluginDeviceValueType.Boolean,
                "true",
                null,
                1),
            sample => AssertSample(
                sample,
                "line.blocked",
                PluginDeviceValueType.Boolean,
                "false",
                null,
                2),
            sample => AssertSample(
                sample,
                "line.speed",
                PluginDeviceValueType.SignedInteger,
                "4660",
                "rpm",
                3),
            sample => AssertSample(
                sample,
                "line.temperature",
                PluginDeviceValueType.SignedInteger,
                "65534",
                "Cel",
                4));
        Assert.Equal(
            [0x01, 0x02, 0x03, 0x04],
            server.Received.Select(static frame => frame.Pdu[0]).ToArray());
        Assert.All(server.Received, static frame =>
        {
            Assert.Equal((ushort)0, frame.ProtocolId);
            Assert.Equal((byte)17, frame.UnitId);
            Assert.Equal((ushort)6, frame.Length);
            Assert.Equal(new byte[] { 0x00, 0x01 }, frame.Pdu[3..5]);
        });
        Assert.Equal(
            4,
            server.Received
                .Select(static frame => frame.TransactionId)
                .Distinct()
                .Count());
    }

    [Fact]
    public async Task WritesSingleAndContiguousMultipleCoilsAndRegisters()
    {
        var signals = new[]
        {
            new ModbusTestSignal("coil.10", "coil", 10),
            new ModbusTestSignal("coil.11", "coil", 11),
            new ModbusTestSignal("register.20", "holdingRegister", 20, "raw"),
            new ModbusTestSignal("register.21", "holdingRegister", 21, "raw")
        };
        await using var server = new LocalModbusTcpServer((_, request) =>
            new ModbusServerReply(ModbusTestFrames.EchoWriteResponse(request)));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-write-device",
            DeviceSessionTestSupport.ModbusConfiguration(
                server.Port,
                signals: signals)));

        var results = new[]
        {
            await plugin.WriteAsync(WriteRequest(
                session.SessionId,
                "single-coil",
                1,
                new PluginDeviceSignalWrite(
                    "coil.10",
                    PluginDeviceValue.FromBoolean(true)))),
            await plugin.WriteAsync(WriteRequest(
                session.SessionId,
                "single-register",
                2,
                new PluginDeviceSignalWrite(
                    "register.20",
                    PluginDeviceValue.FromInt64(123),
                    "raw"))),
            await plugin.WriteAsync(WriteRequest(
                session.SessionId,
                "multiple-coils",
                3,
                new PluginDeviceSignalWrite(
                    "coil.11",
                    PluginDeviceValue.FromBoolean(false)),
                new PluginDeviceSignalWrite(
                    "coil.10",
                    PluginDeviceValue.FromBoolean(true)))),
            await plugin.WriteAsync(WriteRequest(
                session.SessionId,
                "multiple-registers",
                4,
                new PluginDeviceSignalWrite(
                    "register.21",
                    PluginDeviceValue.FromInt64(65_535),
                    "raw"),
                new PluginDeviceSignalWrite(
                    "register.20",
                    PluginDeviceValue.FromInt64(1),
                    "raw")))
        };
        await server.WaitForRequestCountAsync(4);

        Assert.All(results, static result => Assert.True(result.Succeeded));
        Assert.Equal(
            new byte[][]
            {
                [0x05, 0x00, 0x0A, 0xFF, 0x00],
                [0x06, 0x00, 0x14, 0x00, 0x7B],
                [0x0F, 0x00, 0x0A, 0x00, 0x02, 0x01, 0x01],
                [0x10, 0x00, 0x14, 0x00, 0x02, 0x04, 0x00, 0x01, 0xFF, 0xFF]
            },
            server.Received.Select(static frame => frame.Pdu).ToArray());
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("protocol")]
    [InlineData("unit")]
    [InlineData("length")]
    public async Task RejectsInvalidMbapResponseAndMarksSessionUnhealthy(string fault)
    {
        await using var server = new LocalModbusTcpServer((_, request) =>
        {
            var response = fault switch
            {
                "transaction" => ModbusTestFrames.Response(
                    request,
                    [0x03, 0x02, 0x00, 0x01],
                    transactionId: checked((ushort)(request.TransactionId + 1))),
                "protocol" => ModbusTestFrames.Response(
                    request,
                    [0x03, 0x02, 0x00, 0x01],
                    protocolId: 1),
                "unit" => ModbusTestFrames.Response(
                    request,
                    [0x03, 0x02, 0x00, 0x01],
                    unitId: 18),
                "length" => ModbusTestFrames.Response(
                    request,
                    [0x03, 0x02, 0x00, 0x01],
                    declaredLength: 4),
                _ => throw new InvalidOperationException("Unknown test fault.")
            };
            return new ModbusServerReply(response);
        });
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            $"modbus-mbap-{fault}",
            DeviceSessionTestSupport.ModbusConfiguration(
                server.Port,
                reconnectEnabled: false)));

        var exception = await Assert.ThrowsAnyAsync<IOException>(() =>
            plugin.ReadAsync(new PluginDeviceSignalReadRequest(
                    session.SessionId,
                    ["line.speed"]))
                .AsTask());
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));
        var diagnostics = await plugin.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.NotEmpty(exception.Message);
        Assert.Equal(PluginDeviceHealthStatus.Unhealthy, health.Status);
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "ModbusTcp.ProtocolViolation");
    }

    [Theory]
    [InlineData("function")]
    [InlineData("byteCount")]
    [InlineData("padding")]
    public async Task RejectsMalformedReadPdu(string fault)
    {
        await using var server = new LocalModbusTcpServer((_, request) =>
        {
            var pdu = fault switch
            {
                "function" => new byte[] { 0x02, 0x01, 0x01 },
                "byteCount" => [0x01, 0x02, 0x01],
                "padding" => [0x01, 0x01, 0x03],
                _ => throw new InvalidOperationException("Unknown test fault.")
            };
            return new ModbusServerReply(ModbusTestFrames.Response(request, pdu));
        });
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            $"modbus-pdu-{fault}",
            DeviceSessionTestSupport.ModbusConfiguration(
                server.Port,
                reconnectEnabled: false)));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            plugin.ReadAsync(new PluginDeviceSignalReadRequest(
                    session.SessionId,
                    ["line.ready"]))
                .AsTask());

        var diagnostics = await plugin.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(session.SessionId));
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "ModbusTcp.ProtocolViolation");
    }

    [Fact]
    public async Task DeviceExceptionIsReportedWithoutCorruptingTheConnection()
    {
        await using var server = new LocalModbusTcpServer((number, request) =>
        {
            var pdu = number switch
            {
                1 => new byte[] { 0x83, 0x02 },
                2 => [0x86, 0x03],
                _ => [0x03, 0x02, 0x00, 0x2A]
            };
            return new ModbusServerReply(ModbusTestFrames.Response(request, pdu));
        });
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-exception-device",
            DeviceSessionTestSupport.ModbusConfiguration(server.Port)));

        var readException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            plugin.ReadAsync(new PluginDeviceSignalReadRequest(
                    session.SessionId,
                    ["line.speed"]))
                .AsTask());
        var write = await plugin.WriteAsync(WriteRequest(
            session.SessionId,
            "exception-write",
            5,
            new PluginDeviceSignalWrite(
                "line.speed",
                PluginDeviceValue.FromInt64(42),
                "rpm")));
        var recoveredRead = await plugin.ReadAsync(
            new PluginDeviceSignalReadRequest(
                session.SessionId,
                ["line.speed"]));
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));
        var diagnostics = await plugin.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.Contains("IllegalDataAddress", readException.Message);
        Assert.Equal(PluginDeviceOperationOutcome.Rejected, write.Outcome);
        Assert.Contains("IllegalDataValue", write.FailureReason);
        Assert.Equal("42", Assert.Single(recoveredRead).Value.CanonicalValue);
        Assert.Equal(PluginDeviceHealthStatus.Healthy, health.Status);
        Assert.Equal(
            2,
            diagnostics.Entries.Count(static entry =>
                entry.Code == "ModbusTcp.DeviceException"));
        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task InvalidWriteEchoFailsWithoutProtocolRetry()
    {
        await using var server = new LocalModbusTcpServer((_, request) =>
        {
            var responsePdu = request.Pdu.ToArray();
            responsePdu[^1] ^= 0x01;
            return new ModbusServerReply(
                ModbusTestFrames.Response(request, responsePdu));
        });
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-write-echo-device",
            DeviceSessionTestSupport.ModbusConfiguration(server.Port)));

        var result = await plugin.WriteAsync(WriteRequest(
            session.SessionId,
            "invalid-echo",
            8,
            new PluginDeviceSignalWrite(
                "line.speed",
                PluginDeviceValue.FromInt64(42),
                "rpm")));
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));
        var diagnostics = await plugin.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.Equal(PluginDeviceOperationOutcome.Failed, result.Outcome);
        Assert.Equal(PluginDeviceHealthStatus.Unhealthy, health.Status);
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "ModbusTcp.ProtocolViolation");
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task ReadTimeoutClosesConnectionAndReportsDegradedHealth()
    {
        await using var server = new LocalModbusTcpServer((_, request) =>
            new ModbusServerReply(
                ModbusTestFrames.Response(
                    request,
                    [0x03, 0x02, 0x00, 0x01]),
                Delay: TimeSpan.FromSeconds(1)));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-timeout-device",
            DeviceSessionTestSupport.ModbusConfiguration(
                server.Port,
                operationTimeoutMilliseconds: 50)));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            plugin.ReadAsync(new PluginDeviceSignalReadRequest(
                    session.SessionId,
                    ["line.speed"]))
                .AsTask());
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.Equal(PluginDeviceHealthStatus.Degraded, health.Status);
        Assert.Contains("cancelled", health.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationClosesActiveConnection()
    {
        await using var server = new LocalModbusTcpServer((_, request) =>
            new ModbusServerReply(
                ModbusTestFrames.Response(
                    request,
                    [0x03, 0x02, 0x00, 0x01]),
                Delay: TimeSpan.FromSeconds(1)));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-cancel-device",
            DeviceSessionTestSupport.ModbusConfiguration(server.Port)));
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.ReadAsync(
                    new PluginDeviceSignalReadRequest(
                        session.SessionId,
                        ["line.speed"]),
                    cancellation.Token)
                .AsTask());
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.Equal(PluginDeviceHealthStatus.Degraded, health.Status);
    }

    [Fact]
    public async Task IdempotentReadReconnectsAfterAmbiguousDisconnect()
    {
        await using var server = new LocalModbusTcpServer((number, request) =>
            number == 1
                ? new ModbusServerReply(CloseWithoutResponse: true)
                : new ModbusServerReply(ModbusTestFrames.Response(
                    request,
                    [0x03, 0x02, 0x01, 0x23])));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-reconnect-read-device",
            DeviceSessionTestSupport.ModbusConfiguration(server.Port)));

        var sample = Assert.Single(await plugin.ReadAsync(
            new PluginDeviceSignalReadRequest(
                session.SessionId,
                ["line.speed"])));
        await server.WaitForRequestCountAsync(2);
        var health = await plugin.GetHealthAsync(
            new PluginDeviceSessionRequest(session.SessionId));

        Assert.Equal("291", sample.Value.CanonicalValue);
        Assert.Equal(2, server.ConnectionCount);
        Assert.Equal(2, server.Received.Count);
        Assert.Equal(PluginDeviceHealthStatus.Healthy, health.Status);
    }

    [Fact]
    public async Task AmbiguousNonIdempotentWriteIsNeverRetried()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "ambiguous-modbus.jsonl");
        await using var server = new LocalModbusTcpServer((_, _) =>
            new ModbusServerReply(CloseWithoutResponse: true));
        var envelope = DeviceSessionTestSupport.Command(
            "ambiguous-write",
            100,
            PluginDeviceCommandIdempotencyClass.NonIdempotent);
        var write = new PluginDeviceSignalWrite(
            "line.speed",
            PluginDeviceValue.FromInt64(900),
            "rpm");
        PluginDeviceOperationResult first;
        await using (var firstHost = new IndustrialDeviceSessionPlugin())
        {
            var session = await firstHost.OpenAsync(new PluginDeviceSessionOpenRequest(
                "modbus-ambiguous-write-device",
                DeviceSessionTestSupport.ModbusConfiguration(
                    server.Port,
                    journalPath)));
            var request = new PluginDeviceSignalWriteRequest(
                session.SessionId,
                envelope,
                [write]);
            first = await firstHost.WriteAsync(request);
            var replay = await firstHost.WriteAsync(request);

            Assert.Equal(first, replay);
        }

        await server.WaitForRequestCountAsync(1);

        await using var restartedHost = new IndustrialDeviceSessionPlugin();
        var restartedSession = await restartedHost.OpenAsync(
            new PluginDeviceSessionOpenRequest(
                "modbus-ambiguous-write-device",
                DeviceSessionTestSupport.ModbusConfiguration(
                    server.Port,
                    journalPath)));
        var coldReplay = await restartedHost.WriteAsync(
            new PluginDeviceSignalWriteRequest(
                restartedSession.SessionId,
                envelope,
                [write]));
        var diagnostics = await restartedHost.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(restartedSession.SessionId));

        Assert.Equal(PluginDeviceOperationOutcome.Failed, first.Outcome);
        Assert.Equal(PluginDeviceOperationCompletionState.Unknown, first.CompletionState);
        Assert.True(first.RecoveryRequired);
        Assert.Equal(first, coldReplay);
        Assert.Contains("RecoveryRequired", first.FailureReason, StringComparison.Ordinal);
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "DeviceSession.RecoveryRequired");
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task NonIdempotentWriteConnectionFailureBeforeSendDoesNotRequireRecovery()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "pre-send-modbus.jsonl");
        var server = new LocalModbusTcpServer((_, request) =>
            new ModbusServerReply(ModbusTestFrames.EchoWriteResponse(request)));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-pre-send-device",
            DeviceSessionTestSupport.ModbusConfiguration(
                server.Port,
                journalPath,
                reconnectEnabled: false)));
        await server.DisposeAsync();

        var reconnect = await plugin.InvokeAsync(new PluginDeviceInvocationRequest(
            session.SessionId,
            DeviceSessionTestSupport.Command("disconnect-before-send", 199),
            "Reconnect"));
        var result = await plugin.WriteAsync(WriteRequest(
            session.SessionId,
            "pre-send-write",
            200,
            PluginDeviceCommandIdempotencyClass.NonIdempotent,
            new PluginDeviceSignalWrite(
                "line.speed",
                PluginDeviceValue.FromInt64(901),
                "rpm")));

        Assert.Equal(PluginDeviceOperationOutcome.Failed, reconnect.Outcome);
        Assert.Equal(PluginDeviceOperationOutcome.Failed, result.Outcome);
        Assert.Equal(PluginDeviceOperationCompletionState.Known, result.CompletionState);
        Assert.False(result.RecoveryRequired);
    }

    [Fact]
    public async Task InvalidWriteEchoRequiresRecoveryForNonIdempotentCommand()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "invalid-echo-modbus.jsonl");
        await using var server = new LocalModbusTcpServer((_, request) =>
        {
            var responsePdu = request.Pdu.ToArray();
            responsePdu[^1] ^= 0x01;
            return new ModbusServerReply(
                ModbusTestFrames.Response(request, responsePdu));
        });
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-non-idempotent-echo-device",
            DeviceSessionTestSupport.ModbusConfiguration(server.Port, journalPath)));

        var result = await plugin.WriteAsync(WriteRequest(
            session.SessionId,
            "invalid-non-idempotent-echo",
            201,
            PluginDeviceCommandIdempotencyClass.NonIdempotent,
            new PluginDeviceSignalWrite(
                "line.speed",
                PluginDeviceValue.FromInt64(902),
                "rpm")));

        Assert.Equal(PluginDeviceOperationOutcome.Failed, result.Outcome);
        Assert.Equal(PluginDeviceOperationCompletionState.Unknown, result.CompletionState);
        Assert.True(result.RecoveryRequired);
        Assert.Contains("RecoveryRequired", result.FailureReason, StringComparison.Ordinal);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task CompletedNonIdempotentWriteIsReplayedAfterColdRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "modbus-cold-complete.jsonl");
        await using var server = new LocalModbusTcpServer((_, request) =>
            new ModbusServerReply(ModbusTestFrames.EchoWriteResponse(request)));
        var envelope = new PluginDeviceCommandEnvelope(
            "modbus-cold-command",
            200,
            DateTimeOffset.UtcNow.AddMinutes(1),
            PluginDeviceCommandIdempotencyClass.NonIdempotent,
            PluginDeviceCommandSafetyClass.Normal);
        PluginDeviceOperationResult first;
        await using (var firstHost = new IndustrialDeviceSessionPlugin())
        {
            var session = await firstHost.OpenAsync(new PluginDeviceSessionOpenRequest(
                "modbus-cold-device",
                DeviceSessionTestSupport.ModbusConfiguration(
                    server.Port,
                    journalPath)));
            first = await firstHost.WriteAsync(new PluginDeviceSignalWriteRequest(
                session.SessionId,
                envelope,
                [
                    new PluginDeviceSignalWrite(
                        "line.speed",
                        PluginDeviceValue.FromInt64(1_200),
                        "rpm")
                ]));
        }

        await using var restartedHost = new IndustrialDeviceSessionPlugin();
        var restartedSession = await restartedHost.OpenAsync(
            new PluginDeviceSessionOpenRequest(
                "modbus-cold-device",
                DeviceSessionTestSupport.ModbusConfiguration(
                    server.Port,
                    journalPath)));
        var replay = await restartedHost.WriteAsync(
            new PluginDeviceSignalWriteRequest(
                restartedSession.SessionId,
                envelope,
                [
                    new PluginDeviceSignalWrite(
                        "line.speed",
                        PluginDeviceValue.FromInt64(1_200),
                        "rpm")
                ]));
        await server.WaitForRequestCountAsync(1);

        Assert.True(first.Succeeded);
        Assert.Equal(first, replay);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task PendingDurableWriteRequiresRecoveryAfterColdRestart()
    {
        using var directory = new TemporaryTestDirectory();
        var journalPath = Path.Combine(directory.Path, "modbus-cold-pending.jsonl");
        await using var server = new LocalModbusTcpServer((_, request) =>
            new ModbusServerReply(ModbusTestFrames.EchoWriteResponse(request)));
        var envelope = new PluginDeviceCommandEnvelope(
            "modbus-pending-command",
            300,
            DateTimeOffset.UtcNow.AddMinutes(1),
            PluginDeviceCommandIdempotencyClass.NonIdempotent,
            PluginDeviceCommandSafetyClass.Normal);
        await using (var firstHost = new IndustrialDeviceSessionPlugin())
        {
            var session = await firstHost.OpenAsync(new PluginDeviceSessionOpenRequest(
                "modbus-pending-device",
                DeviceSessionTestSupport.ModbusConfiguration(
                    server.Port,
                    journalPath)));
            var result = await firstHost.WriteAsync(
                new PluginDeviceSignalWriteRequest(
                    session.SessionId,
                    envelope,
                    [
                        new PluginDeviceSignalWrite(
                            "line.speed",
                            PluginDeviceValue.FromInt64(500),
                            "rpm")
                    ]));
            Assert.True(result.Succeeded);
        }

        var lines = await File.ReadAllLinesAsync(journalPath);
        Assert.Equal(2, lines.Length);
        await File.WriteAllTextAsync(journalPath, lines[0] + "\n");

        await using var restartedHost = new IndustrialDeviceSessionPlugin();
        var restartedSession = await restartedHost.OpenAsync(
            new PluginDeviceSessionOpenRequest(
                "modbus-pending-device",
                DeviceSessionTestSupport.ModbusConfiguration(
                    server.Port,
                    journalPath)));
        var recovered = await restartedHost.WriteAsync(
            new PluginDeviceSignalWriteRequest(
                restartedSession.SessionId,
                envelope,
                [
                    new PluginDeviceSignalWrite(
                        "line.speed",
                        PluginDeviceValue.FromInt64(500),
                        "rpm")
                ]));
        var diagnostics = await restartedHost.GetDiagnosticsAsync(
            new PluginDeviceSessionRequest(restartedSession.SessionId));

        Assert.Equal(PluginDeviceOperationOutcome.Failed, recovered.Outcome);
        Assert.Equal(
            PluginDeviceOperationCompletionState.Unknown,
            recovered.CompletionState);
        Assert.True(recovered.RecoveryRequired);
        Assert.Contains("RecoveryRequired", recovered.FailureReason);
        Assert.Contains(
            diagnostics.Entries,
            static entry => entry.Code == "DeviceSession.RecoveryRequired");
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task SafetyAndDeadlineRejectionsNeverReachTheDeviceOrAdvanceFence()
    {
        await using var server = new LocalModbusTcpServer((_, request) =>
            new ModbusServerReply(ModbusTestFrames.EchoWriteResponse(request)));
        await using var plugin = new IndustrialDeviceSessionPlugin();
        var session = await plugin.OpenAsync(new PluginDeviceSessionOpenRequest(
            "modbus-command-guard-device",
            DeviceSessionTestSupport.ModbusConfiguration(server.Port)));
        var write = new PluginDeviceSignalWrite(
            "line.speed",
            PluginDeviceValue.FromInt64(600),
            "rpm");

        var safety = await plugin.WriteAsync(new PluginDeviceSignalWriteRequest(
            session.SessionId,
            new PluginDeviceCommandEnvelope(
                "modbus-safety",
                10_000,
                DateTimeOffset.UtcNow.AddMinutes(1),
                PluginDeviceCommandIdempotencyClass.Idempotent,
                PluginDeviceCommandSafetyClass.SafetyCritical),
            [write]));
        var expired = await plugin.WriteAsync(new PluginDeviceSignalWriteRequest(
            session.SessionId,
            new PluginDeviceCommandEnvelope(
                "modbus-expired",
                20_000,
                DateTimeOffset.UtcNow.AddMilliseconds(-1),
                PluginDeviceCommandIdempotencyClass.Idempotent,
                PluginDeviceCommandSafetyClass.Normal),
            [write]));
        var valid = await plugin.WriteAsync(WriteRequest(
            session.SessionId,
            "modbus-valid",
            10,
            write));
        await server.WaitForRequestCountAsync(1);

        Assert.Equal(PluginDeviceOperationOutcome.Rejected, safety.Outcome);
        Assert.Equal(PluginDeviceOperationOutcome.TimedOut, expired.Outcome);
        Assert.True(valid.Succeeded);
        Assert.Single(server.Received);
    }

    private static PluginDeviceSignalWriteRequest WriteRequest(
        string sessionId,
        string commandId,
        long fencingToken,
        params PluginDeviceSignalWrite[] writes) =>
        WriteRequest(
            sessionId,
            commandId,
            fencingToken,
            PluginDeviceCommandIdempotencyClass.Idempotent,
            writes);

    private static PluginDeviceSignalWriteRequest WriteRequest(
        string sessionId,
        string commandId,
        long fencingToken,
        PluginDeviceCommandIdempotencyClass idempotencyClass,
        params PluginDeviceSignalWrite[] writes) =>
        new(
            sessionId,
            DeviceSessionTestSupport.Command(
                commandId,
                fencingToken,
                idempotencyClass),
            writes);

    private static void AssertSample(
        PluginDeviceSignalSample sample,
        string signalId,
        PluginDeviceValueType type,
        string value,
        string? unit,
        long sequence)
    {
        Assert.Equal(signalId, sample.SignalId);
        Assert.Equal(type, sample.Value.Type);
        Assert.Equal(value, sample.Value.CanonicalValue);
        Assert.Equal(unit, sample.Unit);
        Assert.Equal(PluginDeviceSignalQuality.Good, sample.Quality);
        Assert.Equal(sequence, sample.Sequence);
        Assert.Equal(TimeSpan.Zero, sample.SourceTimestampUtc.Offset);
        Assert.Equal(TimeSpan.Zero, sample.ObservedTimestampUtc.Offset);
    }
}
