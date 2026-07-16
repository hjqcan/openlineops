using System.Diagnostics;
using System.Globalization;

namespace OpenLineOps.Api;

internal sealed class DesktopParentProcessLifetime : IDisposable
{
    internal const string ProcessIdEnvironmentVariable =
        "OPENLINEOPS_DESKTOP_PARENT_PROCESS_ID";

    private readonly Process _parentProcess;

    private DesktopParentProcessLifetime(Process parentProcess)
    {
        _parentProcess = parentProcess;
    }

    internal static DesktopParentProcessLifetime? FromEnvironment(
        bool desktopHandshakeConfigured)
    {
        var configuredValue = Environment.GetEnvironmentVariable(
            ProcessIdEnvironmentVariable);
        var processId = ParseConfiguredProcessId(
            configuredValue,
            desktopHandshakeConfigured);
        if (processId is null)
        {
            return null;
        }

        if (processId.Value == Environment.ProcessId)
        {
            throw new InvalidOperationException(
                "Desktop parent process identity cannot be the API process itself.");
        }

        try
        {
            var parentProcess = Process.GetProcessById(processId.Value);
            if (parentProcess.HasExited)
            {
                parentProcess.Dispose();
                throw new InvalidOperationException(
                    "Desktop parent process exited before the API lifetime was bound.");
            }
            return new DesktopParentProcessLifetime(parentProcess);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Desktop parent process does not exist.",
                exception);
        }
    }

    internal async Task MonitorAsync(
        IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        try
        {
            await _parentProcess
                .WaitForExitAsync(applicationLifetime.ApplicationStopping)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            return;
        }

        applicationLifetime.StopApplication();
    }

    internal static int? ParseConfiguredProcessId(
        string? configuredValue,
        bool desktopHandshakeConfigured)
    {
        if (configuredValue is null && !desktopHandshakeConfigured)
        {
            return null;
        }
        if (!desktopHandshakeConfigured || string.IsNullOrWhiteSpace(configuredValue))
        {
            throw new InvalidOperationException(
                "Desktop parent lifetime and process handshake must be configured together.");
        }
        if (!int.TryParse(
                configuredValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processId)
            || processId <= 0
            || !string.Equals(
                configuredValue,
                processId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Desktop parent process identity must be one canonical positive integer.");
        }
        return processId;
    }

    public void Dispose()
    {
        _parentProcess.Dispose();
    }
}
