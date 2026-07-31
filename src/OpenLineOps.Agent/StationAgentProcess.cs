using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace OpenLineOps.Agent;

internal static class StationAgentProcess
{
    public const int SuccessExitCode = 0;
    public const int HostFailureExitCode = 70;

    public static readonly TimeSpan ShutdownTimeout =
        TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FailureReportTimeout =
        TimeSpan.FromSeconds(2);

    public static async Task<int> RunHostAsync(
        IHost host,
        Func<Exception, ValueTask> reportFailureAsync,
        TimeSpan? shutdownTimeout = null,
        TimeSpan? failureReportTimeout = null,
        Action<int>? terminateProcessOnUnrecoverableTimeout = null,
        CancellationToken startupCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reportFailureAsync);
        try
        {
            await StationAgentHostLifecycle.RunAsync(
                    host,
                    shutdownTimeout ?? ShutdownTimeout,
                    startupCancellationToken)
                .ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (Exception exception)
        {
            await ReportFailureWithinDeadlineAsync(
                    exception,
                    reportFailureAsync,
                    failureReportTimeout ?? FailureReportTimeout)
                .ConfigureAwait(false);
            if (terminateProcessOnUnrecoverableTimeout is not null
                && ContainsTimeoutFailure(exception))
            {
                terminateProcessOnUnrecoverableTimeout(HostFailureExitCode);
            }
            return HostFailureExitCode;
        }
    }

    public static async Task ReportFailureWithinDeadlineAsync(
        Exception exception,
        Func<Exception, ValueTask> reportFailureAsync,
        TimeSpan? failureReportTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(reportFailureAsync);
        var timeout = failureReportTimeout ?? FailureReportTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);

        // The reporter may block before it returns a ValueTask (Console and
        // EventLog callbacks are synchronous at that boundary), so invoke the
        // complete sink on a dedicated worker before applying the hard deadline.
        var reporting = StartPotentiallyBlockingOperation(
            async () => await reportFailureAsync(exception).ConfigureAwait(false));
        try
        {
            await reporting.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateFailure(reporting);
        }
        catch
        {
            // Failure reporting is best effort and must never suppress exit 70.
        }
    }

    private static void ObserveLateFailure(Task reporting)
    {
        _ = reporting.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    internal static Task StartPotentiallyBlockingOperation(
        Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // A Host callback can block before returning its Task. A dedicated
        // thread keeps that boundary independent of ThreadPool congestion
        // while the caller enforces the authoritative hard deadline.
        return Task.Factory.StartNew(
                operation,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach
                | TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    private static bool ContainsTimeoutFailure(Exception exception) =>
        exception is TimeoutException
        || exception is AggregateException aggregate
        && aggregate.Flatten().InnerExceptions.Any(ContainsTimeoutFailure);
}

internal static class StationAgentHostLifecycle
{
    public static async Task RunAsync(
        IHost host,
        TimeSpan shutdownTimeout,
        CancellationToken startupCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            shutdownTimeout,
            TimeSpan.Zero);

        var backgroundServices = host.Services
            .GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Select(static service => new MonitoredBackgroundService(
                service.GetType().FullName ?? service.GetType().Name,
                service))
            .ToArray();
        try
        {
            await host.StartAsync(startupCancellationToken).ConfigureAwait(false);
        }
        catch (Exception startupFailure)
        {
            var rollbackFailure = await StopWithinDeadlineAsync(
                    host,
                    shutdownTimeout,
                    "startup rollback")
                .ConfigureAwait(false);
            ThrowFailures(
                "Station Agent Host startup failed and was rolled back.",
                startupFailure,
                rollbackFailure);
            throw;
        }

        var applicationLifetime =
            host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stoppingRequested = WaitForCancellationAsync(
            applicationLifetime.ApplicationStopping);
        var backgroundCompletion = ObserveBackgroundCompletionAsync(
            backgroundServices,
            applicationLifetime.ApplicationStopping);
        var firstCompletion = await Task.WhenAny(
                stoppingRequested,
                backgroundCompletion)
            .ConfigureAwait(false);

        StationAgentBackgroundServiceFailureException? backgroundFailure = null;
        if (ReferenceEquals(firstCompletion, backgroundCompletion))
        {
            backgroundFailure = await backgroundCompletion.ConfigureAwait(false);
        }

        var stopFailure = await StopWithinDeadlineAsync(
                host,
                shutdownTimeout,
                "shutdown")
            .ConfigureAwait(false);
        if (backgroundCompletion.IsCompletedSuccessfully)
        {
            backgroundFailure ??= backgroundCompletion.Result;
        }
        else if (backgroundCompletion.IsFaulted)
        {
            backgroundFailure = new StationAgentBackgroundServiceFailureException(
                "Station Agent background-service supervision failed.",
                backgroundCompletion.Exception!.Flatten());
        }
        else
        {
            _ = backgroundCompletion.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        backgroundFailure ??= CollectBackgroundFailures(backgroundServices);
        ThrowFailures(
            "Station Agent Host failed while running or stopping.",
            backgroundFailure,
            stopFailure);
    }

    private static async Task<StationAgentBackgroundServiceFailureException?>
        ObserveBackgroundCompletionAsync(
            IReadOnlyList<MonitoredBackgroundService> backgroundServices,
            CancellationToken applicationStopping)
    {
        if (backgroundServices.Count == 0)
        {
            await WaitForCancellationAsync(applicationStopping)
                .ConfigureAwait(false);
            return null;
        }

        var executions = new (string Name, Task Execution)[backgroundServices.Count];
        for (var index = 0; index < backgroundServices.Count; index++)
        {
            var monitored = backgroundServices[index];
            var execution = monitored.Service.ExecuteTask;
            if (execution is null)
            {
                return new StationAgentBackgroundServiceFailureException(
                    $"Station Agent background service '{monitored.Name}' did not create an execution task.");
            }

            executions[index] = (monitored.Name, execution);
        }

        var ended = await Task.WhenAny(
                executions.Select(static execution => execution.Execution))
            .ConfigureAwait(false);
        var endedService = executions.First(execution =>
            ReferenceEquals(execution.Execution, ended));
        if (ended.IsFaulted)
        {
            var failures = ended.Exception!
                .Flatten()
                .InnerExceptions
                .Where(static exception => exception is not OperationCanceledException)
                .ToArray();
            if (failures.Length != 0)
            {
                return new StationAgentBackgroundServiceFailureException(
                    $"Station Agent background service '{endedService.Name}' failed.",
                    failures.Length == 1
                        ? failures[0]
                        : new AggregateException(failures));
            }
        }

        if (!applicationStopping.IsCancellationRequested)
        {
            return new StationAgentBackgroundServiceFailureException(
                $"Station Agent background service '{endedService.Name}' ended unexpectedly while the Host was running.");
        }

        return null;
    }

    private static StationAgentBackgroundServiceFailureException?
        CollectBackgroundFailures(
            IReadOnlyList<MonitoredBackgroundService> backgroundServices)
    {
        var failures = new List<(string Name, Exception Failure)>();
        foreach (var monitored in backgroundServices)
        {
            var execution = monitored.Service.ExecuteTask;
            if (execution?.IsFaulted != true)
            {
                continue;
            }

            failures.AddRange(execution.Exception!
                .Flatten()
                .InnerExceptions
                .Where(static exception =>
                    exception is not OperationCanceledException)
                .Select(exception => (monitored.Name, exception)));
        }

        return failures.Count switch
        {
            0 => null,
            1 => new StationAgentBackgroundServiceFailureException(
                $"Station Agent background service '{failures[0].Name}' failed.",
                failures[0].Failure),
            _ => new StationAgentBackgroundServiceFailureException(
                "Multiple Station Agent background services failed: "
                + string.Join(
                    ", ",
                    failures
                        .Select(static failure => failure.Name)
                        .Distinct(StringComparer.Ordinal)),
                new AggregateException(
                    failures.Select(static failure => failure.Failure)))
        };
    }

    private static async Task<Exception?> StopWithinDeadlineAsync(
        IHost host,
        TimeSpan shutdownTimeout,
        string operation)
    {
        using var deadline = new CancellationTokenSource(shutdownTimeout);
        Task? stopTask = null;
        try
        {
            stopTask = StationAgentProcess.StartPotentiallyBlockingOperation(
                () => host.StopAsync(deadline.Token));
            await stopTask
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException exception)
            when (deadline.IsCancellationRequested)
        {
            if (stopTask is not null)
            {
                _ = stopTask.ContinueWith(
                    static completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously
                    | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }

            return new TimeoutException(
                $"Station Agent Host {operation} exceeded the {shutdownTimeout} deadline.",
                exception);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task WaitForCancellationAsync(
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion);
        await completion.Task.ConfigureAwait(false);
    }

    private static void ThrowFailures(
        string message,
        Exception? primaryFailure,
        Exception? stopFailure)
    {
        if (primaryFailure is null && stopFailure is null)
        {
            return;
        }

        if (primaryFailure is not null && stopFailure is not null)
        {
            throw new AggregateException(
                message,
                primaryFailure,
                stopFailure);
        }

        ExceptionDispatchInfo.Capture(primaryFailure ?? stopFailure!).Throw();
    }

    private sealed record MonitoredBackgroundService(
        string Name,
        BackgroundService Service);
}

internal sealed class StationAgentBackgroundServiceFailureException :
    Exception
{
    public StationAgentBackgroundServiceFailureException(string message)
        : base(message)
    {
    }

    public StationAgentBackgroundServiceFailureException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class StationAgentFailureReporter
{
    public static async ValueTask ReportAsync(
        Exception exception,
        string? windowsServiceEventLogSource)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var failureMessage = StationAgentDiagnostics.FormatFailureMessage(
            "OpenLineOps Station Agent terminated",
            exception);
        await Console.Error.WriteLineAsync(failureMessage).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()
            || windowsServiceEventLogSource is null)
        {
            return;
        }

        try
        {
            EventLog.WriteEntry(
                windowsServiceEventLogSource,
                failureMessage,
                EventLogEntryType.Error);
        }
        catch (Exception diagnosticException)
        {
            await Console.Error.WriteLineAsync(
                    StationAgentDiagnostics.FormatFailureMessage(
                        "OpenLineOps Station Agent could not write its failure to EventLog",
                        diagnosticException))
                .ConfigureAwait(false);
        }
    }
}
