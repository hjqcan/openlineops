using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Time;

namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentHostStartupAbortTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductionHostRunnerRollsBackConfirmedPresenceWhenStartupFails()
    {
        var shutdownState = new StationAgentShutdownState();
        var publisher = new RecordingPresencePublisher();
        using var startupCancellation = new CancellationTokenSource();
        using var presenceWorker = new StationAgentPresenceWorker(
            new StationAgentPresenceOptions(
                "agent.main",
                "station.main",
                "station-system.main",
                TimeSpan.FromMilliseconds(20)),
            publisher,
            new FixedClock(Now),
            shutdownState,
            NullLogger<StationAgentPresenceWorker>.Instance);
        using var stationWorker = CreateLifecycleOnlyWorker(shutdownState);
        var startupAbort = new CancelStartupAfterHeartbeatService(
            startupCancellation,
            publisher.HeartbeatPublished.Task);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(presenceWorker);
        builder.Services.AddSingleton<IHostedService>(startupAbort);
        builder.Services.AddSingleton<IHostedService>(stationWorker);
        using var host = builder.Build();
        Exception? reportedFailure = null;

        var exitCode = await StationAgentProcess.RunHostAsync(
                host,
                exception =>
                {
                    reportedFailure = exception;
                    return ValueTask.CompletedTask;
                },
                shutdownTimeout: TimeSpan.FromSeconds(2),
                startupCancellationToken: startupCancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StationAgentProcess.HostFailureExitCode, exitCode);
        Assert.NotNull(reportedFailure);
        Assert.True(
            reportedFailure is OperationCanceledException
            || reportedFailure is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions.Any(static exception =>
                exception is OperationCanceledException),
            reportedFailure.ToString());
        Assert.Equal(1, startupAbort.StopCalls);
        Assert.Equal(
            StationAgentWorkerState.Quiesced,
            shutdownState.WorkerState);
        Assert.Null(stationWorker.ExecuteTask);
        var messages = publisher.Messages.ToArray();
        Assert.Equal(AgentPresenceState.Started, messages[0].State);
        Assert.Contains(
            messages,
            static message => message.State == AgentPresenceState.Heartbeat);
        Assert.Equal(AgentPresenceState.Stopping, messages[^1].State);
        Assert.Single(
            messages,
            static message => message.State == AgentPresenceState.Stopping);
    }

    [Fact]
    public async Task ProductionHostRunnerReturnsSeventyAfterBackgroundFaultAndOrderedStopping()
    {
        var shutdownState = new StationAgentShutdownState();
        var publisher = new RecordingPresencePublisher();
        using var presenceWorker = new StationAgentPresenceWorker(
            new StationAgentPresenceOptions(
                "agent.main",
                "station.main",
                "station-system.main",
                TimeSpan.FromMilliseconds(20)),
            publisher,
            new FixedClock(Now),
            shutdownState,
            NullLogger<StationAgentPresenceWorker>.Instance);
        using var stationWorker = CreateLifecycleOnlyWorker(shutdownState);
        var waitForHeartbeat = new WaitForHeartbeatStartupService(
            publisher.HeartbeatPublished.Task);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior =
                BackgroundServiceExceptionBehavior.StopHost;
            options.ServicesStartConcurrently = false;
            options.ServicesStopConcurrently = false;
        });
        builder.Services.AddSingleton<IHostedService>(presenceWorker);
        builder.Services.AddSingleton<IHostedService>(waitForHeartbeat);
        builder.Services.AddSingleton<IHostedService>(stationWorker);
        using var host = builder.Build();
        Exception? reportedFailure = null;

        var exitCode = await StationAgentProcess.RunHostAsync(
                host,
                exception =>
                {
                    reportedFailure = exception;
                    return ValueTask.CompletedTask;
                },
                shutdownTimeout: TimeSpan.FromSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StationAgentProcess.HostFailureExitCode, exitCode);
        var backgroundFailure = Assert.IsType<
            StationAgentBackgroundServiceFailureException>(reportedFailure);
        Assert.Contains(
            nameof(StationAgentWorker),
            backgroundFailure.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, waitForHeartbeat.StopCalls);
        Assert.Equal(
            StationAgentWorkerState.Quiesced,
            shutdownState.WorkerState);
        var messages = publisher.Messages.ToArray();
        Assert.Equal(AgentPresenceState.Started, messages[0].State);
        Assert.Contains(
            messages,
            static message => message.State == AgentPresenceState.Heartbeat);
        Assert.Equal(AgentPresenceState.Stopping, messages[^1].State);
        Assert.Single(
            messages,
            static message => message.State == AgentPresenceState.Stopping);
    }

    [Fact]
    public async Task CanceledWorkerStartupRollsBackRunningStateWithoutExecutingDependencies()
    {
        var shutdownState = new StationAgentShutdownState();
        using var stationWorker = CreateLifecycleOnlyWorker(shutdownState);
        using var startupCancellation = new CancellationTokenSource();
        startupCancellation.Cancel();

        var startFailure = await Record.ExceptionAsync(
            () => stationWorker.StartAsync(startupCancellation.Token));

        Assert.True(
            startFailure is null or OperationCanceledException,
            startFailure?.ToString());
        var execution = stationWorker.ExecuteTask
            ?? throw new InvalidOperationException(
                "Canceled worker startup did not create an execution task.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution);
        using var quiescenceDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        shutdownState.BeginShutdown();
        await shutdownState.WaitForWorkerQuiescenceAsync(
            quiescenceDeadline.Token);
        Assert.Equal(
            StationAgentWorkerState.Quiesced,
            shutdownState.WorkerState);
    }

    [Fact]
    public async Task ProductionHostRunnerBoundsStartupRollbackWhenServiceIgnoresCancellation()
    {
        var stubbornService = new StubbornStopService();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(stubbornService);
        builder.Services.AddSingleton<IHostedService>(
            new FailingStartupService());
        using var host = builder.Build();
        Exception? reportedFailure = null;
        int? requestedImmediateExitCode = null;

        try
        {
            var exitCode = await StationAgentProcess.RunHostAsync(
                    host,
                    exception =>
                    {
                        reportedFailure = exception;
                        return ValueTask.CompletedTask;
                    },
                    shutdownTimeout: TimeSpan.FromMilliseconds(50),
                    terminateProcessOnUnrecoverableTimeout:
                        exitCode => requestedImmediateExitCode = exitCode)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(StationAgentProcess.HostFailureExitCode, exitCode);
            Assert.Equal(
                StationAgentProcess.HostFailureExitCode,
                requestedImmediateExitCode);
            var aggregate = Assert.IsType<AggregateException>(reportedFailure);
            Assert.Contains(
                aggregate.Flatten().InnerExceptions,
                static exception => exception is TimeoutException
                                    && exception.Message.Contains(
                                        "startup rollback",
                                        StringComparison.Ordinal));
            Assert.Equal(1, stubbornService.StopCalls);
        }
        finally
        {
            stubbornService.ReleaseStop();
        }
    }

    [Fact]
    public async Task ProductionHostRunnerBoundsSynchronouslyBlockedStartupRollback()
    {
        using var stubbornService = new SynchronouslyBlockedStopService();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(stubbornService);
        builder.Services.AddSingleton<IHostedService>(
            new FailingStartupService());
        using var host = builder.Build();
        Exception? reportedFailure = null;
        int? requestedImmediateExitCode = null;

        try
        {
            var exitCode = await StationAgentProcess.RunHostAsync(
                    host,
                    exception =>
                    {
                        reportedFailure = exception;
                        return ValueTask.CompletedTask;
                    },
                    shutdownTimeout: TimeSpan.FromMilliseconds(50),
                    terminateProcessOnUnrecoverableTimeout:
                        exitCode => requestedImmediateExitCode = exitCode)
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(StationAgentProcess.HostFailureExitCode, exitCode);
            Assert.Equal(
                StationAgentProcess.HostFailureExitCode,
                requestedImmediateExitCode);
            var aggregate = Assert.IsType<AggregateException>(reportedFailure);
            Assert.Contains(
                aggregate.Flatten().InnerExceptions,
                static exception => exception is TimeoutException
                                    && exception.Message.Contains(
                                        "startup rollback",
                                        StringComparison.Ordinal));
            Assert.True(
                stubbornService.StopEntered.Wait(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            stubbornService.Release();
        }
    }

    [Fact]
    public async Task NonCancellationBackgroundFaultDuringStopStillReturnsSeventy()
    {
        var backgroundService = new FaultDuringStopBackgroundService();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior =
                BackgroundServiceExceptionBehavior.StopHost;
            options.ServicesStartConcurrently = false;
            options.ServicesStopConcurrently = false;
        });
        builder.Services.AddSingleton<IHostedService>(backgroundService);
        builder.Services.AddSingleton<IHostedService>(serviceProvider =>
            new RequestHostStopService(
                serviceProvider.GetRequiredService<IHostApplicationLifetime>(),
                backgroundService.Started.Task));
        using var host = builder.Build();
        Exception? reportedFailure = null;

        var exitCode = await StationAgentProcess.RunHostAsync(
                host,
                exception =>
                {
                    reportedFailure = exception;
                    return ValueTask.CompletedTask;
                },
                shutdownTimeout: TimeSpan.FromSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StationAgentProcess.HostFailureExitCode, exitCode);
        var backgroundFailure = Assert.IsType<
            StationAgentBackgroundServiceFailureException>(reportedFailure);
        Assert.Equal(
            "Synthetic failure after Host stopping was requested.",
            backgroundFailure.InnerException?.Message);
    }

    [Fact]
    public async Task BlockingApplicationStoppingCallbackCannotDelayBackgroundFailureExit()
    {
        var backgroundService = new TriggeredFailureBackgroundService();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior =
                BackgroundServiceExceptionBehavior.StopHost;
            options.ServicesStartConcurrently = false;
            options.ServicesStopConcurrently = false;
        });
        builder.Services.AddSingleton<IHostedService>(backgroundService);
        using var host = builder.Build();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var registration = host.Services
            .GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() =>
            {
                callbackEntered.Set();
                releaseCallback.Wait();
            });
        int? requestedImmediateExitCode = null;
        try
        {
            var run = StationAgentProcess.RunHostAsync(
                host,
                _ => ValueTask.CompletedTask,
                shutdownTimeout: TimeSpan.FromMilliseconds(50),
                terminateProcessOnUnrecoverableTimeout:
                    exitCode => requestedImmediateExitCode = exitCode);
            await backgroundService.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(1));

            backgroundService.Fail();
            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(StationAgentProcess.HostFailureExitCode, exitCode);
            Assert.Equal(
                StationAgentProcess.HostFailureExitCode,
                requestedImmediateExitCode);
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            releaseCallback.Set();
        }
    }

    [Fact]
    public async Task FailureReporterCannotDelayHostFailureExit()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(
            new FailingStartupService());
        using var host = builder.Build();
        var reporterEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReporter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var exitCode = await StationAgentProcess.RunHostAsync(
                    host,
                    async _ =>
                    {
                        reporterEntered.TrySetResult();
                        await releaseReporter.Task.ConfigureAwait(false);
                    },
                    shutdownTimeout: TimeSpan.FromMilliseconds(100),
                    failureReportTimeout: TimeSpan.FromMilliseconds(50))
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(StationAgentProcess.HostFailureExitCode, exitCode);
            await reporterEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseReporter.TrySetResult();
        }
    }

    [Fact]
    public async Task SynchronouslyBlockedFailureReporterCannotDelayHostFailureExit()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(
            new FailingStartupService());
        using var host = builder.Build();
        using var reporterEntered = new ManualResetEventSlim();
        using var releaseReporter = new ManualResetEventSlim();

        try
        {
            var exitCode = await StationAgentProcess.RunHostAsync(
                    host,
                    _ =>
                    {
                        reporterEntered.Set();
                        releaseReporter.Wait(CancellationToken.None);
                        return ValueTask.CompletedTask;
                    },
                    shutdownTimeout: TimeSpan.FromMilliseconds(100),
                    failureReportTimeout: TimeSpan.FromMilliseconds(50))
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(StationAgentProcess.HostFailureExitCode, exitCode);
            Assert.True(reporterEntered.Wait(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            releaseReporter.Set();
        }
    }

    private static StationAgentWorker CreateLifecycleOnlyWorker(
        StationAgentShutdownState shutdownState) =>
        new(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            shutdownState,
            NullLogger<StationAgentWorker>.Instance);

    private sealed class CancelStartupAfterHeartbeatService(
        CancellationTokenSource startupCancellation,
        Task heartbeatPublished) : IHostedService
    {
        private int _stopCalls;

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await heartbeatPublished
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            startupCancellation.Cancel();
            throw new OperationCanceledException(startupCancellation.Token);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _stopCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class WaitForHeartbeatStartupService(Task heartbeatPublished) :
        IHostedService
    {
        private int _stopCalls;

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await heartbeatPublished
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _stopCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStartupService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.FromException(
                new InvalidOperationException("Synthetic startup failure."));

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubbornStopService : IHostedService
    {
        private readonly TaskCompletionSource _stopCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopCalls;

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopCalls);
            await _stopCompletion.Task.ConfigureAwait(false);
        }

        public void ReleaseStop() => _stopCompletion.TrySetResult();
    }

    private sealed class SynchronouslyBlockedStopService :
        IHostedService,
        IDisposable
    {
        private readonly ManualResetEventSlim _release = new();

        public ManualResetEventSlim StopEntered { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopEntered.Set();
            _release.Wait(CancellationToken.None);
            return Task.CompletedTask;
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            StopEntered.Dispose();
        }
    }

    private sealed class FaultDuringStopBackgroundService : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                throw new InvalidDataException(
                    "Synthetic failure after Host stopping was requested.");
            }
        }
    }

    private sealed class TriggeredFailureBackgroundService : BackgroundService
    {
        private readonly TaskCompletionSource _fail = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail() => _fail.TrySetResult();

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            await _fail.Task.ConfigureAwait(false);
            throw new InvalidDataException("Synthetic background failure.");
        }
    }

    private sealed class RequestHostStopService(
        IHostApplicationLifetime applicationLifetime,
        Task backgroundStarted) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await backgroundStarted
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            applicationLifetime.StopApplication();
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingPresencePublisher :
        IStationAgentMessagePublisher
    {
        public ConcurrentQueue<AgentPresenceReported> Messages { get; } = new();

        public TaskCompletionSource HeartbeatPublished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(nameof(AgentPresenceReported), kind);
            var message = JsonSerializer.Deserialize<AgentPresenceReported>(
                payloadJson,
                JsonOptions)
                ?? throw new InvalidDataException(
                    "Presence test payload is null.");
            Messages.Enqueue(message);
            if (message.State == AgentPresenceState.Heartbeat)
            {
                HeartbeatPublished.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
