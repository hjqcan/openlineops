using System.Runtime.Versioning;

namespace OpenLineOps.WindowsServiceToken.TestHelper;

[SupportedOSPlatform("windows")]
internal sealed class OneShotWindowsServiceWorker(
    WindowsServiceTokenTransferOperation operation,
    IHostApplicationLifetime applicationLifetime,
    ILogger<OneShotWindowsServiceWorker> logger) : BackgroundService
{
    private const int OperationFailureExitCode = 70;
    private static readonly Action<ILogger, Exception?> LogOperationFailure =
        LoggerMessage.Define(
            LogLevel.Critical,
            new EventId(1, "WindowsServiceTokenTransferFailed"),
            "The one-shot Windows service token transfer failed closed.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            await operation.ExecuteAsync(stoppingToken);
        }
        catch (Exception exception)
        {
            Environment.ExitCode = OperationFailureExitCode;
            LogOperationFailure(logger, exception);
        }
        finally
        {
            applicationLifetime.StopApplication();
        }
    }
}
