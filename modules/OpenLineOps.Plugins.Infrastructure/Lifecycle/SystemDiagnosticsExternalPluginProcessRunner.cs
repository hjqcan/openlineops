using System.Diagnostics;
using System.Text.Json;
using OpenLineOps.Plugins.Application.Commands;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public sealed class SystemDiagnosticsExternalPluginProcessRunner : IExternalPluginProcessRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ExternalProcessPluginHostOptions _options;
    private readonly IExternalPluginProcessEventSink _eventSink;

    public SystemDiagnosticsExternalPluginProcessRunner(
        ExternalProcessPluginHostOptions options,
        IExternalPluginProcessEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventSink);
        _options = options;
        _eventSink = eventSink;
    }

    public async ValueTask<IExternalPluginProcess> StartAsync(
        ExternalPluginProcessStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var process = new Process
        {
            StartInfo = ExternalPluginProcessStartInfoBuilder.Build(_options, request),
            EnableRaisingEvents = true
        };

        Record(
            ExternalPluginProcessEventKind.Starting,
            request.Manifest.Id,
            $"Starting external plugin process '{request.Manifest.Id}'.");

        if (!process.Start())
        {
            process.Dispose();
            Record(
                ExternalPluginProcessEventKind.StartupFailed,
                request.Manifest.Id,
                $"Plugin process '{request.Manifest.Id}' could not be started.");

            throw new InvalidOperationException(
                $"Plugin process '{request.Manifest.Id}' could not be started.");
        }

        using var registration = cancellationToken.Register(static state =>
        {
            if (state is Process process && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }, process);

        if (_options.StartupProbeDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.StartupProbeDelay, cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                process.Dispose();
                Record(
                    ExternalPluginProcessEventKind.StartupExited,
                    request.Manifest.Id,
                    $"Plugin process '{request.Manifest.Id}' exited during startup with code {exitCode}.",
                    detail: exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

                throw new InvalidOperationException(
                    $"Plugin process '{request.Manifest.Id}' exited during startup with code {exitCode}.");
            }
        }

        Record(
            ExternalPluginProcessEventKind.Started,
            request.Manifest.Id,
            $"External plugin process '{request.Manifest.Id}' started.");

        return new SystemDiagnosticsExternalPluginProcess(
            process,
            _options.ShutdownTimeout,
            _options.Sandbox,
            _eventSink);
    }

    private void Record(
        ExternalPluginProcessEventKind kind,
        string pluginId,
        string message,
        string? detail = null)
    {
        _eventSink.Record(new ExternalPluginProcessEvent(
            kind,
            pluginId,
            message,
            DateTimeOffset.UtcNow,
            detail));
    }

    private sealed class SystemDiagnosticsExternalPluginProcess(
        Process process,
        TimeSpan shutdownTimeout,
        ExternalPluginSandboxOptions sandboxOptions,
        IExternalPluginProcessEventSink eventSink) : IExternalPluginProcess
    {
        private readonly SemaphoreSlim _commandLock = new(1, 1);

        public bool HasExited => process.HasExited;

        public async ValueTask<PluginDeviceCommandInvocationResult> ExecuteDeviceCommandAsync(
            PluginDeviceCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                return PluginDeviceCommandInvocationResult.Failed(
                    $"External plugin process '{request.PluginId}' has exited.");
            }

            var effectiveTimeoutMilliseconds = ResolveCommandTimeoutMilliseconds(request.TimeoutMilliseconds);
            var effectiveRequest = request with
            {
                TimeoutMilliseconds = effectiveTimeoutMilliseconds
            };
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (effectiveTimeoutMilliseconds > 0)
            {
                timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeoutMilliseconds));
            }

            try
            {
                await _commandLock.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                try
                {
                    var requestId = Guid.NewGuid().ToString("N");
                    var message = new ExternalDeviceCommandProtocolRequest(
                        "device-command",
                        requestId,
                        effectiveRequest);
                    var requestLine = JsonSerializer.Serialize(message, JsonOptions);

                    await process.StandardInput
                        .WriteLineAsync(requestLine.AsMemory(), timeoutSource.Token)
                        .ConfigureAwait(false);
                    await process.StandardInput
                        .FlushAsync(timeoutSource.Token)
                        .ConfigureAwait(false);

                    var responseLine = await process.StandardOutput
                        .ReadLineAsync(timeoutSource.Token)
                        .ConfigureAwait(false);
                    if (responseLine is null)
                    {
                        return PluginDeviceCommandInvocationResult.Failed(
                            $"External plugin process '{request.PluginId}' closed stdout before returning a command result.");
                    }

                    var response = JsonSerializer.Deserialize<ExternalDeviceCommandProtocolResponse>(
                        responseLine,
                        JsonOptions);
                    if (response is null)
                    {
                        return PluginDeviceCommandInvocationResult.Failed(
                            $"External plugin process '{request.PluginId}' returned an empty command response.");
                    }

                    if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal)
                        || !string.Equals(response.MessageType, "device-command-result", StringComparison.Ordinal))
                    {
                        return PluginDeviceCommandInvocationResult.Failed(
                            $"External plugin process '{request.PluginId}' returned an invalid command response.");
                    }

                    if (!string.IsNullOrWhiteSpace(response.Error))
                    {
                        return PluginDeviceCommandInvocationResult.Failed(response.Error);
                    }

                    return response.Payload
                        ?? PluginDeviceCommandInvocationResult.Failed(
                            $"External plugin process '{request.PluginId}' returned no command result payload.");
                }
                finally
                {
                    _commandLock.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TerminateTimedOutProcess(request.PluginId, effectiveTimeoutMilliseconds);

                return PluginDeviceCommandInvocationResult.TimedOut(
                    $"External plugin process '{request.PluginId}' command timed out after {effectiveTimeoutMilliseconds}ms.");
            }
        }

        public async ValueTask<PluginProcessCommandInvocationResult> ExecuteProcessCommandAsync(
            PluginProcessCommandInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                return PluginProcessCommandInvocationResult.Failed(
                    $"External plugin process '{request.PluginId}' has exited.");
            }

            var effectiveTimeoutMilliseconds = ResolveCommandTimeoutMilliseconds(request.TimeoutMilliseconds);
            var effectiveRequest = request with
            {
                TimeoutMilliseconds = effectiveTimeoutMilliseconds
            };
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (effectiveTimeoutMilliseconds > 0)
            {
                timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeoutMilliseconds));
            }

            try
            {
                await _commandLock.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                try
                {
                    var requestId = Guid.NewGuid().ToString("N");
                    var message = new ExternalProcessCommandProtocolRequest(
                        "process-command",
                        requestId,
                        effectiveRequest);
                    var requestLine = JsonSerializer.Serialize(message, JsonOptions);

                    await process.StandardInput
                        .WriteLineAsync(requestLine.AsMemory(), timeoutSource.Token)
                        .ConfigureAwait(false);
                    await process.StandardInput
                        .FlushAsync(timeoutSource.Token)
                        .ConfigureAwait(false);

                    var responseLine = await process.StandardOutput
                        .ReadLineAsync(timeoutSource.Token)
                        .ConfigureAwait(false);
                    if (responseLine is null)
                    {
                        return PluginProcessCommandInvocationResult.Failed(
                            $"External plugin process '{request.PluginId}' closed stdout before returning a command result.");
                    }

                    var response = JsonSerializer.Deserialize<ExternalProcessCommandProtocolResponse>(
                        responseLine,
                        JsonOptions);
                    if (response is null)
                    {
                        return PluginProcessCommandInvocationResult.Failed(
                            $"External plugin process '{request.PluginId}' returned an empty command response.");
                    }

                    if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal)
                        || !string.Equals(response.MessageType, "process-command-result", StringComparison.Ordinal))
                    {
                        return PluginProcessCommandInvocationResult.Failed(
                            $"External plugin process '{request.PluginId}' returned an invalid command response.");
                    }

                    if (!string.IsNullOrWhiteSpace(response.Error))
                    {
                        return PluginProcessCommandInvocationResult.Failed(response.Error);
                    }

                    return response.Payload
                        ?? PluginProcessCommandInvocationResult.Failed(
                            $"External plugin process '{request.PluginId}' returned no command result payload.");
                }
                finally
                {
                    _commandLock.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TerminateTimedOutProcess(request.PluginId, effectiveTimeoutMilliseconds);

                return PluginProcessCommandInvocationResult.TimedOut(
                    $"External plugin process '{request.PluginId}' command timed out after {effectiveTimeoutMilliseconds}ms.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(shutdownTimeout).ConfigureAwait(false);
                }
            }
            finally
            {
                _commandLock.Dispose();
                process.Dispose();
            }
        }

        private int ResolveCommandTimeoutMilliseconds(int requestedTimeoutMilliseconds)
        {
            var requested = requestedTimeoutMilliseconds <= 0
                ? 0
                : requestedTimeoutMilliseconds;
            var max = ToTimeoutMilliseconds(sandboxOptions.MaxCommandTimeout);

            if (max <= 0)
            {
                return requested;
            }

            if (requested <= 0)
            {
                return max;
            }

            return Math.Min(requested, max);
        }

        private void TerminateTimedOutProcess(string pluginId, int timeoutMilliseconds)
        {
            eventSink.Record(new ExternalPluginProcessEvent(
                ExternalPluginProcessEventKind.CommandTimedOut,
                pluginId,
                $"External plugin process '{pluginId}' command timed out after {timeoutMilliseconds}ms.",
                DateTimeOffset.UtcNow));

            if (!sandboxOptions.TerminateProcessOnCommandTimeout || process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            eventSink.Record(new ExternalPluginProcessEvent(
                ExternalPluginProcessEventKind.ProcessKilled,
                pluginId,
                $"External plugin process '{pluginId}' was terminated after command timeout.",
                DateTimeOffset.UtcNow));
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 0;
        }

        if (timeout.TotalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private sealed record ExternalDeviceCommandProtocolRequest(
        string MessageType,
        string RequestId,
        PluginDeviceCommandInvocationRequest Payload);

    private sealed record ExternalDeviceCommandProtocolResponse(
        string MessageType,
        string RequestId,
        PluginDeviceCommandInvocationResult? Payload,
        string? Error);

    private sealed record ExternalProcessCommandProtocolRequest(
        string MessageType,
        string RequestId,
        PluginProcessCommandInvocationRequest Payload);

    private sealed record ExternalProcessCommandProtocolResponse(
        string MessageType,
        string RequestId,
        PluginProcessCommandInvocationResult? Payload,
        string? Error);
}
