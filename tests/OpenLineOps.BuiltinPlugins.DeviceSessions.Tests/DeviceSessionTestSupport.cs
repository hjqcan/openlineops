using System.Text.Json;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions.Tests;

internal static class DeviceSessionTestSupport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ScpiConfiguration(
        int port,
        string? journalPath = null,
        int operationTimeoutMilliseconds = 2_000,
        bool reconnectEnabled = true,
        int reconnectAttempts = 3) =>
        JsonSerializer.Serialize(
            new
            {
                Adapter = "scpiTcp",
                ScpiTcp = new
                {
                    Host = "127.0.0.1",
                    Port = port,
                    ConnectTimeoutMilliseconds = 1_000,
                    OperationTimeoutMilliseconds = operationTimeoutMilliseconds,
                    HeartbeatMilliseconds = 100,
                    LineTerminator = "lf",
                    Encoding = "ascii",
                    Reconnect = new
                    {
                        Enabled = reconnectEnabled,
                        MaxAttempts = reconnectAttempts,
                        InitialDelayMilliseconds = 10,
                        MaximumDelayMilliseconds = 50
                    }
                },
                Recording = journalPath is null
                    ? null
                    : new
                    {
                        JournalPath = journalPath
                    }
            },
            JsonOptions);

    public static string ScannerConfiguration(
        int port,
        string? journalPath = null,
        int reconnectAttempts = 10) =>
        JsonSerializer.Serialize(
            new
            {
                Adapter = "tcpLineScanner",
                TcpLineScanner = new
                {
                    Host = "127.0.0.1",
                    Port = port,
                    SignalId = "scanner.code",
                    ConnectTimeoutMilliseconds = 1_000,
                    HeartbeatMilliseconds = 100,
                    HistoryCapacity = 100,
                    Encoding = "utf8",
                    Reconnect = new
                    {
                        Enabled = true,
                        MaxAttempts = reconnectAttempts,
                        InitialDelayMilliseconds = 10,
                        MaximumDelayMilliseconds = 50
                    }
                },
                Recording = journalPath is null
                    ? null
                    : new
                    {
                        JournalPath = journalPath
                    }
            },
            JsonOptions);

    public static string ModbusConfiguration(
        int port,
        string? journalPath = null,
        int operationTimeoutMilliseconds = 2_000,
        bool reconnectEnabled = true,
        int reconnectAttempts = 3,
        int unitId = 17,
        IReadOnlyList<ModbusTestSignal>? signals = null) =>
        JsonSerializer.Serialize(
            new
            {
                Adapter = "modbusTcp",
                ModbusTcp = new
                {
                    Host = "127.0.0.1",
                    Port = port,
                    UnitId = unitId,
                    ConnectTimeoutMilliseconds = 1_000,
                    OperationTimeoutMilliseconds = operationTimeoutMilliseconds,
                    HeartbeatMilliseconds = 100,
                    HistoryCapacity = 100,
                    Reconnect = new
                    {
                        Enabled = reconnectEnabled,
                        MaxAttempts = reconnectAttempts,
                        InitialDelayMilliseconds = 10,
                        MaximumDelayMilliseconds = 50
                    },
                    Signals = signals ??
                    [
                        new ModbusTestSignal("line.ready", "coil", 0),
                        new ModbusTestSignal("line.blocked", "discreteInput", 1),
                        new ModbusTestSignal(
                            "line.speed",
                            "holdingRegister",
                            100,
                            "rpm"),
                        new ModbusTestSignal(
                            "line.temperature",
                            "inputRegister",
                            200,
                            "Cel")
                    ]
                },
                Recording = journalPath is null
                    ? null
                    : new
                    {
                        JournalPath = journalPath
                    }
            },
            JsonOptions);

    public static string ReplayConfiguration(
        string journalPath,
        double speedFactor = 1,
        int additionalDelayMilliseconds = 0,
        long? disconnectAtJournalSequence = null,
        params string[] badQualitySignalIds) =>
        JsonSerializer.Serialize(
            new
            {
                Adapter = "replay",
                Replay = new
                {
                    JournalPath = journalPath,
                    SpeedFactor = speedFactor,
                    AdditionalDelayMilliseconds = additionalDelayMilliseconds,
                    DisconnectAtJournalSequence = disconnectAtJournalSequence,
                    DisconnectDurationMilliseconds = 25,
                    BadQualitySignalIds = badQualitySignalIds,
                    HeartbeatMilliseconds = 100,
                    HistoryCapacity = 100
                }
            },
            JsonOptions);

    public static PluginDeviceCommandEnvelope Command(
        string commandId,
        long fencingToken,
        PluginDeviceCommandIdempotencyClass idempotencyClass =
            PluginDeviceCommandIdempotencyClass.Idempotent,
        TimeSpan? deadline = null) =>
        new(
            commandId,
            fencingToken,
            DateTimeOffset.UtcNow.Add(deadline ?? TimeSpan.FromSeconds(5)),
            idempotencyClass,
            PluginDeviceCommandSafetyClass.Normal);
}

internal sealed record ModbusTestSignal(
    string SignalId,
    string Kind,
    int Address,
    string? Unit = null);

internal sealed class TemporaryTestDirectory : IDisposable
{
    public TemporaryTestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"openlineops-device-session-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
