using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

public sealed record DeviceSessionConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string Adapter { get; init; } = string.Empty;

    public ScpiTcpConfiguration? ScpiTcp { get; init; }

    public TcpLineScannerConfiguration? TcpLineScanner { get; init; }

    public ModbusTcpConfiguration? ModbusTcp { get; init; }

    public ReplayConfiguration? Replay { get; init; }

    public RecordingConfiguration? Recording { get; init; }

    internal static DeviceSessionConfiguration Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidDataException("Device session configuration is required.");
        }

        DeviceSessionConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<DeviceSessionConfiguration>(
                    payload,
                    JsonOptions)
                ?? throw new InvalidDataException("Device session configuration cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Device session configuration is invalid: {exception.Message}",
                exception);
        }

        configuration.Validate();
        return configuration;
    }

    private void Validate()
    {
        switch (Adapter)
        {
            case DeviceSessionAdapterNames.ScpiTcp:
                if (ScpiTcp is null
                    || TcpLineScanner is not null
                    || ModbusTcp is not null
                    || Replay is not null)
                {
                    throw new InvalidDataException(
                        "The scpiTcp adapter requires only the scpiTcp configuration section.");
                }

                ScpiTcp.Validate();
                break;
            case DeviceSessionAdapterNames.TcpLineScanner:
                if (TcpLineScanner is null
                    || ScpiTcp is not null
                    || ModbusTcp is not null
                    || Replay is not null)
                {
                    throw new InvalidDataException(
                        "The tcpLineScanner adapter requires only the tcpLineScanner configuration section.");
                }

                TcpLineScanner.Validate();
                break;
            case DeviceSessionAdapterNames.ModbusTcp:
                if (ModbusTcp is null
                    || ScpiTcp is not null
                    || TcpLineScanner is not null
                    || Replay is not null)
                {
                    throw new InvalidDataException(
                        "The modbusTcp adapter requires only the modbusTcp configuration section.");
                }

                ModbusTcp.Validate();
                break;
            case DeviceSessionAdapterNames.Replay:
                if (Replay is null
                    || ScpiTcp is not null
                    || TcpLineScanner is not null
                    || ModbusTcp is not null)
                {
                    throw new InvalidDataException(
                        "The replay adapter requires only the replay configuration section.");
                }

                Replay.Validate();
                if (Recording is not null)
                {
                    throw new InvalidDataException(
                        "A replay session cannot record into another journal.");
                }

                break;
            default:
                throw new InvalidDataException(
                    $"Device session adapter '{Adapter}' is not supported.");
        }

        Recording?.Validate();
    }
}

public static class DeviceSessionAdapterNames
{
    public const string ScpiTcp = "scpiTcp";
    public const string TcpLineScanner = "tcpLineScanner";
    public const string ModbusTcp = "modbusTcp";
    public const string Replay = "replay";
}

public sealed record ScpiTcpConfiguration
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    public int ConnectTimeoutMilliseconds { get; init; } = 5_000;

    public int OperationTimeoutMilliseconds { get; init; } = 30_000;

    public int HeartbeatMilliseconds { get; init; } = 5_000;

    public string LineTerminator { get; init; } = "lf";

    public string Encoding { get; init; } = "ascii";

    public ReconnectConfiguration Reconnect { get; init; } = new();

    internal void Validate()
    {
        DeviceSessionConfigurationGuard.Host(Host);
        DeviceSessionConfigurationGuard.Port(Port);
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            ConnectTimeoutMilliseconds,
            nameof(ConnectTimeoutMilliseconds));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            OperationTimeoutMilliseconds,
            nameof(OperationTimeoutMilliseconds));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            HeartbeatMilliseconds,
            nameof(HeartbeatMilliseconds));
        DeviceSessionConfigurationGuard.LineTerminator(LineTerminator);
        DeviceSessionConfigurationGuard.Encoding(Encoding);
        (Reconnect ?? throw new InvalidDataException("Reconnect configuration is required."))
            .Validate();
    }
}

public sealed record TcpLineScannerConfiguration
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; }

    public string SignalId { get; init; } = "scanner.code";

    public int ConnectTimeoutMilliseconds { get; init; } = 5_000;

    public int HeartbeatMilliseconds { get; init; } = 5_000;

    public int HistoryCapacity { get; init; } = 1_024;

    public string Encoding { get; init; } = "utf8";

    public ReconnectConfiguration Reconnect { get; init; } = new()
    {
        Enabled = true,
        MaxAttempts = 0
    };

    internal void Validate()
    {
        DeviceSessionConfigurationGuard.Host(Host);
        DeviceSessionConfigurationGuard.Port(Port);
        DeviceSessionConfigurationGuard.Canonical(SignalId, nameof(SignalId));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            ConnectTimeoutMilliseconds,
            nameof(ConnectTimeoutMilliseconds));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            HeartbeatMilliseconds,
            nameof(HeartbeatMilliseconds));
        if (HistoryCapacity is < 1 or > 1_000_000)
        {
            throw new InvalidDataException(
                "HistoryCapacity must be between 1 and 1000000.");
        }

        DeviceSessionConfigurationGuard.Encoding(Encoding);
        (Reconnect ?? throw new InvalidDataException("Reconnect configuration is required."))
            .Validate();
    }
}

public static class ModbusSignalKinds
{
    public const string Coil = "coil";
    public const string DiscreteInput = "discreteInput";
    public const string HoldingRegister = "holdingRegister";
    public const string InputRegister = "inputRegister";

    internal static bool IsSupported(string value) =>
        value is Coil or DiscreteInput or HoldingRegister or InputRegister;
}

public sealed record ModbusSignalConfiguration
{
    public string SignalId { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    // Modbus protocol addresses are zero-based. Human-facing 00001/40001 notation
    // must be normalized by engineering tooling before this configuration is signed.
    public int Address { get; init; }

    public string? Unit { get; init; }

    internal void Validate()
    {
        DeviceSessionConfigurationGuard.Canonical(SignalId, nameof(SignalId));
        if (!ModbusSignalKinds.IsSupported(Kind))
        {
            throw new InvalidDataException(
                $"Signal '{SignalId}' kind must be one of: coil, discreteInput, holdingRegister, inputRegister.");
        }

        if (Address is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"Signal '{SignalId}' address must be between 0 and 65535.");
        }

        if (Unit is not null)
        {
            DeviceSessionConfigurationGuard.Canonical(Unit, nameof(Unit));
        }
    }
}

public sealed record ModbusTcpConfiguration
{
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 502;

    public int UnitId { get; init; } = 1;

    public int ConnectTimeoutMilliseconds { get; init; } = 5_000;

    public int OperationTimeoutMilliseconds { get; init; } = 30_000;

    public int HeartbeatMilliseconds { get; init; } = 5_000;

    public int HistoryCapacity { get; init; } = 1_024;

    public ReconnectConfiguration Reconnect { get; init; } = new();

    public IReadOnlyList<ModbusSignalConfiguration> Signals { get; init; } = [];

    internal void Validate()
    {
        DeviceSessionConfigurationGuard.Host(Host);
        DeviceSessionConfigurationGuard.Port(Port);
        if (UnitId is < byte.MinValue or > byte.MaxValue)
        {
            throw new InvalidDataException("UnitId must be between 0 and 255.");
        }

        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            ConnectTimeoutMilliseconds,
            nameof(ConnectTimeoutMilliseconds));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            OperationTimeoutMilliseconds,
            nameof(OperationTimeoutMilliseconds));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            HeartbeatMilliseconds,
            nameof(HeartbeatMilliseconds));
        if (HistoryCapacity is < 1 or > 1_000_000)
        {
            throw new InvalidDataException(
                "HistoryCapacity must be between 1 and 1000000.");
        }

        (Reconnect ?? throw new InvalidDataException("Reconnect configuration is required."))
            .Validate();
        if (Signals is null || Signals.Count is < 1 or > 10_000)
        {
            throw new InvalidDataException(
                "Signals must contain between 1 and 10000 definitions.");
        }

        foreach (var signal in Signals)
        {
            (signal ?? throw new InvalidDataException(
                "Signals cannot contain null definitions.")).Validate();
        }

        if (Signals.Select(static signal => signal.SignalId)
            .Distinct(StringComparer.Ordinal)
            .Count() != Signals.Count)
        {
            throw new InvalidDataException("Signal IDs must be unique.");
        }

        if (Signals.Select(static signal => (signal.Kind, signal.Address))
            .Distinct()
            .Count() != Signals.Count)
        {
            throw new InvalidDataException(
                "Each Modbus kind and address pair can map to only one signal.");
        }
    }
}

public sealed record ReconnectConfiguration
{
    public bool Enabled { get; init; } = true;

    // Zero means unlimited reconnect attempts. This is useful for passive devices.
    public int MaxAttempts { get; init; } = 3;

    public int InitialDelayMilliseconds { get; init; } = 100;

    public int MaximumDelayMilliseconds { get; init; } = 5_000;

    internal void Validate()
    {
        if (MaxAttempts is < 0 or > 10_000)
        {
            throw new InvalidDataException("MaxAttempts must be between 0 and 10000.");
        }

        DeviceSessionConfigurationGuard.NonNegativeMilliseconds(
            InitialDelayMilliseconds,
            nameof(InitialDelayMilliseconds));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            MaximumDelayMilliseconds,
            nameof(MaximumDelayMilliseconds));
        if (InitialDelayMilliseconds > MaximumDelayMilliseconds)
        {
            throw new InvalidDataException(
                "InitialDelayMilliseconds cannot exceed MaximumDelayMilliseconds.");
        }
    }
}

public sealed record RecordingConfiguration
{
    public string JournalPath { get; init; } = string.Empty;

    internal void Validate()
    {
        DeviceSessionConfigurationGuard.Canonical(JournalPath, nameof(JournalPath));
        if (!Path.IsPathFullyQualified(JournalPath))
        {
            throw new InvalidDataException("JournalPath must be an absolute path.");
        }
    }
}

public sealed record ReplayConfiguration
{
    public string JournalPath { get; init; } = string.Empty;

    public double SpeedFactor { get; init; } = 1;

    public int AdditionalDelayMilliseconds { get; init; }

    public long? DisconnectAtJournalSequence { get; init; }

    public int DisconnectDurationMilliseconds { get; init; } = 250;

    public IReadOnlyList<string> BadQualitySignalIds { get; init; } = [];

    public int HeartbeatMilliseconds { get; init; } = 1_000;

    public int HistoryCapacity { get; init; } = 10_000;

    internal void Validate()
    {
        DeviceSessionConfigurationGuard.Canonical(JournalPath, nameof(JournalPath));
        if (!Path.IsPathFullyQualified(JournalPath))
        {
            throw new InvalidDataException("JournalPath must be an absolute path.");
        }

        if (!double.IsFinite(SpeedFactor) || SpeedFactor <= 0 || SpeedFactor > 10_000)
        {
            throw new InvalidDataException(
                "SpeedFactor must be finite and greater than zero, up to 10000.");
        }

        DeviceSessionConfigurationGuard.NonNegativeMilliseconds(
            AdditionalDelayMilliseconds,
            nameof(AdditionalDelayMilliseconds));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            DisconnectDurationMilliseconds,
            nameof(DisconnectDurationMilliseconds));
        DeviceSessionConfigurationGuard.PositiveMilliseconds(
            HeartbeatMilliseconds,
            nameof(HeartbeatMilliseconds));
        if (DisconnectAtJournalSequence <= 0)
        {
            throw new InvalidDataException(
                "DisconnectAtJournalSequence must be positive when configured.");
        }

        if (HistoryCapacity is < 1 or > 1_000_000)
        {
            throw new InvalidDataException(
                "HistoryCapacity must be between 1 and 1000000.");
        }

        if (BadQualitySignalIds is null
            || BadQualitySignalIds.Any(static value =>
                string.IsNullOrWhiteSpace(value)
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            || BadQualitySignalIds.Distinct(StringComparer.Ordinal).Count()
               != BadQualitySignalIds.Count)
        {
            throw new InvalidDataException(
                "BadQualitySignalIds must contain unique canonical signal ids.");
        }
    }
}

internal static class DeviceSessionConfigurationGuard
{
    public static void Host(string value)
    {
        Canonical(value, "Host");
        if (value.Length > 253)
        {
            throw new InvalidDataException("Host is too long.");
        }
    }

    public static void Port(int value)
    {
        if (value is < 1 or > 65_535)
        {
            throw new InvalidDataException("Port must be between 1 and 65535.");
        }
    }

    public static void Canonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{name} must be non-empty canonical text.");
        }
    }

    public static void PositiveMilliseconds(int value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidDataException($"{name} must be positive.");
        }
    }

    public static void NonNegativeMilliseconds(int value, string name)
    {
        if (value < 0)
        {
            throw new InvalidDataException($"{name} cannot be negative.");
        }
    }

    public static void LineTerminator(string value)
    {
        if (value is not ("lf" or "crlf" or "cr"))
        {
            throw new InvalidDataException(
                "LineTerminator must be one of: lf, crlf, cr.");
        }
    }

    public static void Encoding(string value)
    {
        if (value is not ("ascii" or "utf8"))
        {
            throw new InvalidDataException("Encoding must be ascii or utf8.");
        }
    }
}
