using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Api;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.Api.TestHelper;

internal static class Program
{
    internal const int GracefulRootExitCode = 41;
    private const int UsageExitCode = 64;

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UsageExitCode;
        }

        return args switch
        {
            [
                "parent",
                var shutdownMode,
                var rootProcessIdFile,
                var childProcessIdFile,
                var stopRequestedFile,
                var releaseParentFile
            ] => await RunParentAsync(
                shutdownMode,
                rootProcessIdFile,
                childProcessIdFile,
                stopRequestedFile,
                releaseParentFile),
            [
                "api-root",
                var shutdownMode,
                var rootProcessIdFile,
                var childProcessIdFile,
                var stopRequestedFile
            ] => await RunApiRootAsync(
                shutdownMode,
                rootProcessIdFile,
                childProcessIdFile,
                stopRequestedFile),
            _ => UsageExitCode
        };
    }

    private static async Task<int> RunParentAsync(
        string shutdownMode,
        string rootProcessIdFile,
        string childProcessIdFile,
        string stopRequestedFile,
        string releaseParentFile)
    {
        if (!IsShutdownMode(shutdownMode))
        {
            return UsageExitCode;
        }

        var startInfo = CreateSelfStartInfo(
            "api-root",
            shutdownMode,
            rootProcessIdFile,
            childProcessIdFile,
            stopRequestedFile);
        startInfo.Environment[DesktopParentProcessLifetime.ProcessIdEnvironmentVariable] =
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[
            DesktopParentProcessLifetime.ProcessStartedAtEnvironmentVariable] =
            CurrentProcessStartedAtUnixMilliseconds()
                .ToString(CultureInfo.InvariantCulture);
        using var root = Process.Start(startInfo)
                         ?? throw new InvalidOperationException("API root helper did not start.");
        var releasePath = Path.GetFullPath(releaseParentFile);
        while (!File.Exists(releasePath))
        {
            await Task.Delay(10);
        }

        return 0;
    }

    private static async Task<int> RunApiRootAsync(
        string shutdownMode,
        string rootProcessIdFile,
        string childProcessIdFile,
        string stopRequestedFile)
    {
        if (!IsShutdownMode(shutdownMode))
        {
            return UsageExitCode;
        }

        if (string.Equals(shutdownMode, "startup-stuck", StringComparison.Ordinal))
        {
            return await RunStartupStuckApiRootAsync(
                rootProcessIdFile,
                childProcessIdFile,
                stopRequestedFile);
        }

        var processTreeLifetime = WindowsCurrentProcessTreeLifetime.BindCurrentProcess();
        using var parentLifetime = DesktopParentProcessLifetime.FromEnvironment(
            desktopHandshakeConfigured: true)
            ?? throw new InvalidOperationException("Desktop parent lifetime was not configured.");
        using var child = StartLongRunningChild();
        using var applicationLifetime = new ControlledApplicationLifetime(
            stopRequestedFile,
            completeStop: string.Equals(
                shutdownMode,
                "graceful",
                StringComparison.Ordinal),
            blockStoppingCallback: string.Equals(
                shutdownMode,
                "callback-stuck",
                StringComparison.Ordinal));
        if (string.Equals(shutdownMode, "stuck", StringComparison.Ordinal))
        {
            applicationLifetime.BeginStopping();
        }
        await WriteProcessIdAsync(rootProcessIdFile, Environment.ProcessId);
        await WriteProcessIdAsync(childProcessIdFile, child.Id);

        await parentLifetime.MonitorAsync(applicationLifetime);
        GC.KeepAlive(processTreeLifetime);
        return GracefulRootExitCode;
    }

    private static long CurrentProcessStartedAtUnixMilliseconds()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime())
            .ToUnixTimeMilliseconds();
    }

    private static async Task<int> RunStartupStuckApiRootAsync(
        string rootProcessIdFile,
        string childProcessIdFile,
        string stopRequestedFile)
    {
        var processTreeLifetime = WindowsCurrentProcessTreeLifetime.BindCurrentProcess();
        using var parentLifetime = DesktopParentProcessLifetime.FromEnvironment(
            desktopHandshakeConfigured: true)
            ?? throw new InvalidOperationException("Desktop parent lifetime was not configured.");
        using var child = StartLongRunningChild();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService>(
            new BlockingStartupService(
                rootProcessIdFile,
                childProcessIdFile,
                child.Id));
        using var host = builder.Build();
        var applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var stoppingRegistration = applicationLifetime.ApplicationStopping.Register(
            () => File.WriteAllText(
                Path.GetFullPath(stopRequestedFile),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));

        var parentMonitor = parentLifetime.MonitorAsync(applicationLifetime);
        await host.StartAsync();
        await parentMonitor;
        GC.KeepAlive(processTreeLifetime);
        return GracefulRootExitCode;
    }

    private static bool IsShutdownMode(string value) =>
        string.Equals(value, "graceful", StringComparison.Ordinal)
        || string.Equals(value, "stuck", StringComparison.Ordinal)
        || string.Equals(value, "callback-stuck", StringComparison.Ordinal)
        || string.Equals(value, "startup-stuck", StringComparison.Ordinal);

    private static ProcessStartInfo CreateSelfStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                       ?? throw new InvalidOperationException("Test helper path is unavailable."),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static Process StartLongRunningChild()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("127.0.0.1");
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("API descendant helper did not start.");
    }

    private static async Task WriteProcessIdAsync(string path, int processId)
    {
        await File.WriteAllTextAsync(
            Path.GetFullPath(path),
            processId.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class ControlledApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        private readonly string _stopRequestedFile;
        private readonly bool _completeStop;
        private readonly CancellationTokenRegistration _blockingStoppingRegistration;

        internal ControlledApplicationLifetime(
            string stopRequestedFile,
            bool completeStop,
            bool blockStoppingCallback)
        {
            _stopRequestedFile = stopRequestedFile;
            _completeStop = completeStop;
            _blockingStoppingRegistration = blockStoppingCallback
                ? _stopping.Token.Register(
                    static () => Thread.Sleep(Timeout.Infinite))
                : default;
        }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            File.WriteAllText(
                Path.GetFullPath(_stopRequestedFile),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            _stopping.Cancel();
            if (_completeStop)
            {
                _stopped.Cancel();
            }
        }

        internal void BeginStopping()
        {
            _stopping.Cancel();
        }

        public void Dispose()
        {
            _blockingStoppingRegistration.Dispose();
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private sealed class BlockingStartupService(
        string rootProcessIdFile,
        string childProcessIdFile,
        int childProcessId) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            File.WriteAllText(
                Path.GetFullPath(rootProcessIdFile),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(
                Path.GetFullPath(childProcessIdFile),
                childProcessId.ToString(CultureInfo.InvariantCulture));
            return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
