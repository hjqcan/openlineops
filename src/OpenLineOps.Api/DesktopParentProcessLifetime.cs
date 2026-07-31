using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace OpenLineOps.Api;

internal sealed class DesktopParentProcessLifetime : IDisposable
{
    internal const string ProcessIdEnvironmentVariable =
        "OPENLINEOPS_DESKTOP_PARENT_PROCESS_ID";
    internal const string ProcessStartedAtEnvironmentVariable =
        "OPENLINEOPS_DESKTOP_PARENT_PROCESS_STARTED_AT_UNIX_MS";
    internal static readonly TimeSpan ParentExitShutdownDeadline = TimeSpan.FromSeconds(10);

    private readonly Process? _parentProcess;
    private readonly Func<CancellationToken, Task> _waitForParentExitAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<string> _failFast;
    private readonly TimeSpan _shutdownDeadline;

    private DesktopParentProcessLifetime(Process parentProcess)
        : this(
            parentProcess,
            parentProcess.WaitForExitAsync,
            ParentExitShutdownDeadline,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            TerminateCurrentProcess)
    {
    }

    internal DesktopParentProcessLifetime(
        Func<CancellationToken, Task> waitForParentExitAsync,
        TimeSpan shutdownDeadline,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<string> failFast)
        : this(
            null,
            waitForParentExitAsync,
            shutdownDeadline,
            delayAsync,
            failFast)
    {
    }

    private DesktopParentProcessLifetime(
        Process? parentProcess,
        Func<CancellationToken, Task> waitForParentExitAsync,
        TimeSpan shutdownDeadline,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Action<string> failFast)
    {
        ArgumentNullException.ThrowIfNull(waitForParentExitAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(failFast);
        if (shutdownDeadline <= TimeSpan.Zero
            || shutdownDeadline == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shutdownDeadline),
                "Desktop parent-exit shutdown deadline must be positive and finite.");
        }

        _parentProcess = parentProcess;
        _waitForParentExitAsync = waitForParentExitAsync;
        _shutdownDeadline = shutdownDeadline;
        _delayAsync = delayAsync;
        _failFast = failFast;
    }

    internal static DesktopParentProcessLifetime? FromEnvironment(
        bool desktopHandshakeConfigured)
    {
        var configuredValue = Environment.GetEnvironmentVariable(
            ProcessIdEnvironmentVariable);
        var configuredStartTime = Environment.GetEnvironmentVariable(
            ProcessStartedAtEnvironmentVariable);
        var identity = ParseConfiguredIdentity(
            configuredValue,
            configuredStartTime,
            desktopHandshakeConfigured);
        if (identity is null)
        {
            return null;
        }

        if (identity.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException(
                "Desktop parent process identity cannot be the API process itself.");
        }

        Process? parentProcess = null;
        try
        {
            parentProcess = Process.GetProcessById(identity.ProcessId);
            if (parentProcess.HasExited)
            {
                parentProcess.Dispose();
                parentProcess = null;
                throw new InvalidOperationException(
                    "Desktop parent process exited before the API lifetime was bound.");
            }
            var actualStartedAtUnixMilliseconds = new DateTimeOffset(
                    parentProcess.StartTime.ToUniversalTime())
                .ToUnixTimeMilliseconds();
            if (!StartTimesMatch(
                    actualStartedAtUnixMilliseconds,
                    identity.StartedAtUnixMilliseconds))
            {
                parentProcess.Dispose();
                parentProcess = null;
                throw new InvalidOperationException(
                    "Desktop parent process creation identity does not match "
                    + "the process that launched the API.");
            }
            using var currentProcess = Process.GetCurrentProcess();
            if (parentProcess.StartTime.ToUniversalTime()
                >= currentProcess.StartTime.ToUniversalTime())
            {
                parentProcess.Dispose();
                parentProcess = null;
                throw new InvalidOperationException(
                    "Desktop parent process must have been created before the API process.");
            }
            var lifetime = new DesktopParentProcessLifetime(parentProcess);
            parentProcess = null;
            return lifetime;
        }
        catch (ArgumentException exception)
        {
            parentProcess?.Dispose();
            throw new InvalidOperationException(
                "Desktop parent process does not exist.",
                exception);
        }
        catch (Win32Exception exception)
        {
            parentProcess?.Dispose();
            throw new InvalidOperationException(
                "Desktop parent process identity could not be verified.",
                exception);
        }
        catch (InvalidOperationException)
        {
            parentProcess?.Dispose();
            throw;
        }
    }

    internal async Task MonitorAsync(
        IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        try
        {
            await _waitForParentExitAsync(applicationLifetime.ApplicationStopped)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (applicationLifetime.ApplicationStopped.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Losing the verified parent wait is equivalent to losing parent
            // ownership. Continue into the same fail-closed shutdown path.
        }

        if (applicationLifetime.ApplicationStopped.IsCancellationRequested)
        {
            return;
        }

        // Start the independent hard deadline before requesting graceful stop.
        // IHostApplicationLifetime invokes ApplicationStopping callbacks
        // synchronously, so StopApplication itself is allowed to block forever.
        var shutdownDeadline = EnforceParentExitShutdownDeadlineAsync(
            applicationLifetime);
        applicationLifetime.StopApplication();
        await shutdownDeadline.ConfigureAwait(false);
    }

    internal static DesktopParentProcessIdentity? ParseConfiguredIdentity(
        string? configuredProcessId,
        string? configuredStartedAtUnixMilliseconds,
        bool desktopHandshakeConfigured)
    {
        if (configuredProcessId is null
            && configuredStartedAtUnixMilliseconds is null
            && !desktopHandshakeConfigured)
        {
            return null;
        }
        if (!desktopHandshakeConfigured
            || string.IsNullOrWhiteSpace(configuredProcessId)
            || string.IsNullOrWhiteSpace(
                configuredStartedAtUnixMilliseconds))
        {
            throw new InvalidOperationException(
                "Desktop parent lifetime, creation identity, and process "
                + "handshake must be configured together.");
        }
        if (!int.TryParse(
                configuredProcessId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var processId)
            || processId <= 0
            || !string.Equals(
                configuredProcessId,
                processId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Desktop parent process identity must be one canonical positive integer.");
        }
        if (!long.TryParse(
                configuredStartedAtUnixMilliseconds,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var startedAtUnixMilliseconds)
            || startedAtUnixMilliseconds <= 0
            || !string.Equals(
                configuredStartedAtUnixMilliseconds,
                startedAtUnixMilliseconds.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Desktop parent process creation identity must be one "
                + "canonical positive Unix millisecond value.");
        }
        try
        {
            _ = DateTimeOffset.FromUnixTimeMilliseconds(
                startedAtUnixMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException(
                "Desktop parent process creation identity is outside the "
                + "supported Unix timestamp range.",
                exception);
        }

        return new DesktopParentProcessIdentity(
            processId,
            startedAtUnixMilliseconds);
    }

    public void Dispose()
    {
        _parentProcess?.Dispose();
    }

    private async Task EnforceParentExitShutdownDeadlineAsync(
        IHostApplicationLifetime applicationLifetime)
    {
        if (applicationLifetime.ApplicationStopped.IsCancellationRequested)
        {
            return;
        }

        using var deadlineCancellation = new CancellationTokenSource();
        var deadline = _delayAsync(_shutdownDeadline, deadlineCancellation.Token);
        var applicationStopped = Task.Delay(
            Timeout.InfiniteTimeSpan,
            applicationLifetime.ApplicationStopped);
        _ = await Task.WhenAny(applicationStopped, deadline).ConfigureAwait(false);
        if (applicationStopped.IsCompleted)
        {
            await deadlineCancellation.CancelAsync().ConfigureAwait(false);
            return;
        }

        await deadline.ConfigureAwait(false);
        if (applicationStopped.IsCompleted)
        {
            return;
        }

        _failFast(
            $"OpenLineOps.Api did not stop within {_shutdownDeadline.TotalSeconds:g} seconds "
            + "after its Desktop parent process exited.");
        throw new UnreachableException("The fail-fast process terminator unexpectedly returned.");
    }

    [DoesNotReturn]
    private static void TerminateCurrentProcess(string message)
    {
        _ = message;
        using var process = Process.GetCurrentProcess();
        process.Kill(entireProcessTree: false);
        while (true)
        {
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static bool StartTimesMatch(long actual, long expected) =>
        actual == expected;
}

internal sealed record DesktopParentProcessIdentity(
    int ProcessId,
    long StartedAtUnixMilliseconds);
