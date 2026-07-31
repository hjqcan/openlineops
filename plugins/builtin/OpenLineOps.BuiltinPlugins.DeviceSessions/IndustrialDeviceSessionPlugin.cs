using System.Collections.Concurrent;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

public sealed class IndustrialDeviceSessionPlugin : IOpenLineOpsDeviceSessionPlugin
{
    private readonly ConcurrentDictionary<string, IDeviceSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public IndustrialDeviceSessionPlugin()
        : this(TimeProvider.System)
    {
    }

    internal IndustrialDeviceSessionPlugin(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public PluginManifest Manifest { get; } = new(
        Id: "openlineops.builtin.device-sessions",
        Name: "Industrial Device Sessions",
        Version: "0.1.0",
        Kind: PluginKind.DeviceDriver,
        EntryAssembly: "OpenLineOps.BuiltinPlugins.DeviceSessions.dll",
        EntryType: typeof(IndustrialDeviceSessionPlugin).FullName!,
        Capabilities:
        [
            "device.scpi-tcp",
            "device.modbus-tcp",
            "device.tcp-line-scanner",
            "device.record-replay"
        ],
        DeviceCommands:
        [
            new PluginDeviceCommandDefinition(
                "device.sessions:query",
                "device.scpi-tcp",
                "Query",
                "application/json",
                "application/json",
                30_000),
            new PluginDeviceCommandDefinition(
                "device.sessions:write",
                "device.scpi-tcp",
                "Write",
                "application/json",
                "application/json",
                30_000),
            new PluginDeviceCommandDefinition(
                "device.sessions:modbus-write",
                "device.modbus-tcp",
                "Write",
                "application/json",
                "application/json",
                30_000),
            new PluginDeviceCommandDefinition(
                "device.sessions:modbus-reconnect",
                "device.modbus-tcp",
                "Reconnect",
                "application/json",
                "application/json",
                10_000),
            new PluginDeviceCommandDefinition(
                "device.sessions:reconnect",
                "device.tcp-line-scanner",
                "Reconnect",
                "application/json",
                "application/json",
                10_000)
        ]);

    public ValueTask<PluginInitializationStatus> InitializeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PluginInitializationStatus.Initialized);
    }

    public async ValueTask<PluginDeviceSession> OpenAsync(
        PluginDeviceSessionOpenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = DeviceSessionConfiguration.Parse(
            request.ConfigurationPayload);
        var journal = await DeviceSessionJournal.OpenWriterAsync(
                configuration.Recording,
                cancellationToken)
            .ConfigureAwait(false);
        IDeviceSession? session = null;
        try
        {
            session = configuration.Adapter switch
            {
                DeviceSessionAdapterNames.ScpiTcp =>
                    await ScpiTcpDeviceSession.OpenAsync(
                            request.DeviceInstanceId,
                            configuration.ScpiTcp!,
                            journal,
                            _timeProvider,
                            cancellationToken)
                        .ConfigureAwait(false),
                DeviceSessionAdapterNames.TcpLineScanner =>
                    await TcpLineScannerDeviceSession.OpenAsync(
                            request.DeviceInstanceId,
                            configuration.TcpLineScanner!,
                            journal,
                            _timeProvider,
                            cancellationToken)
                        .ConfigureAwait(false),
                DeviceSessionAdapterNames.ModbusTcp =>
                    await ModbusTcpDeviceSession.OpenAsync(
                            request.DeviceInstanceId,
                            configuration.ModbusTcp!,
                            journal,
                            _timeProvider,
                            cancellationToken)
                        .ConfigureAwait(false),
                DeviceSessionAdapterNames.Replay =>
                    await ReplayDeviceSession.OpenAsync(
                            request.DeviceInstanceId,
                            configuration.Replay!,
                            _timeProvider,
                            cancellationToken)
                        .ConfigureAwait(false),
                _ => throw new InvalidDataException(
                    $"Unsupported device adapter '{configuration.Adapter}'.")
            };
            if (!_sessions.TryAdd(session.Contract.SessionId, session))
            {
                throw new InvalidOperationException(
                    $"Device session '{session.Contract.SessionId}' already exists.");
            }

            return session.Contract;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else if (journal is not null)
            {
                await journal.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask CloseAsync(
        PluginDeviceSessionCloseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.TryRemove(request.SessionId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask<IReadOnlyCollection<PluginDeviceSignalSample>> ReadAsync(
        PluginDeviceSignalReadRequest request,
        CancellationToken cancellationToken = default) =>
        GetRequiredSession(request?.SessionId).ReadAsync(request!, cancellationToken);

    public ValueTask<PluginDeviceOperationResult> WriteAsync(
        PluginDeviceSignalWriteRequest request,
        CancellationToken cancellationToken = default) =>
        GetRequiredSession(request?.SessionId).WriteAsync(request!, cancellationToken);

    public IAsyncEnumerable<PluginDeviceSignalSubscriptionEvent> SubscribeAsync(
        PluginDeviceSignalSubscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        GetRequiredSession(request?.SessionId).SubscribeAsync(request!, cancellationToken);

    public ValueTask<PluginDeviceOperationResult> InvokeAsync(
        PluginDeviceInvocationRequest request,
        CancellationToken cancellationToken = default) =>
        GetRequiredSession(request?.SessionId).InvokeAsync(request!, cancellationToken);

    public ValueTask<PluginDeviceHealthSnapshot> GetHealthAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetRequiredSession(request.SessionId).GetHealth());
    }

    public ValueTask<PluginDeviceDiagnosticsSnapshot> GetDiagnosticsAsync(
        PluginDeviceSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetRequiredSession(request.SessionId).GetDiagnostics());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _sessions.ToArray())
        {
            if (_sessions.TryRemove(pair.Key, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private IDeviceSession GetRequiredSession(string? sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new InvalidOperationException(
                $"Device session '{sessionId}' is not open.");
    }
}
