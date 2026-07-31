using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using OpenLineOps.Api;

namespace OpenLineOps.Api.Tests;

[Collection(DesktopParentProcessEnvironmentTestGroup.Name)]
public sealed class DesktopParentProcessLifetimeTests
{
    [Fact]
    public void NoDesktopBindingLeavesParentLifetimeDisabled()
    {
        Assert.Null(
            DesktopParentProcessLifetime.ParseConfiguredIdentity(
                null,
                null,
                false));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", "1", true)]
    [InlineData("1", null, true)]
    [InlineData("1", "1", false)]
    public void ParentLifetimeAndHandshakeMustBeConfiguredTogether(
        string? configuredProcessId,
        string? configuredStartedAt,
        bool handshakeConfigured)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DesktopParentProcessLifetime.ParseConfiguredIdentity(
                configuredProcessId,
                configuredStartedAt,
                handshakeConfigured));

        Assert.Contains("configured together", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("2147483648")]
    public void ParentProcessIdentityMustBeCanonical(string configuredValue)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DesktopParentProcessLifetime.ParseConfiguredIdentity(
                configuredValue,
                "1700000000000",
                true));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    public void ParentProcessCreationIdentityMustBeCanonical(
        string configuredValue)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DesktopParentProcessLifetime.ParseConfiguredIdentity(
                "12345",
                configuredValue,
                true));
    }

    [Fact]
    public void CanonicalParentProcessIdentityIsAccepted()
    {
        Assert.Equal(
            new DesktopParentProcessIdentity(12345, 1700000000000),
            DesktopParentProcessLifetime.ParseConfiguredIdentity(
                "12345",
                "1700000000000",
                true));
    }

    [Fact]
    public void ParentProcessCreationMismatchIsRejectedBeforeBinding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var parent = StartLongRunningProcess();
        try
        {
            var actualStartedAt = new DateTimeOffset(
                    parent.StartTime.ToUniversalTime())
                .ToUnixTimeMilliseconds();
            using var environment = new EnvironmentVariableScope(
                new Dictionary<string, string?>
                {
                    [DesktopParentProcessLifetime.ProcessIdEnvironmentVariable] =
                        parent.Id.ToString(CultureInfo.InvariantCulture),
                    [DesktopParentProcessLifetime.ProcessStartedAtEnvironmentVariable] =
                        (actualStartedAt + 1).ToString(CultureInfo.InvariantCulture)
                });

            var exception = Assert.Throws<InvalidOperationException>(() =>
                DesktopParentProcessLifetime.FromEnvironment(
                    desktopHandshakeConfigured: true));

            Assert.Contains(
                "creation identity does not match",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Terminate(parent);
        }
    }

    [Fact]
    public void NewerProcessCannotBeSubstitutedAsDesktopParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var parent = StartLongRunningProcess();
        try
        {
            var actualStartedAt = new DateTimeOffset(
                    parent.StartTime.ToUniversalTime())
                .ToUnixTimeMilliseconds();
            using var environment = new EnvironmentVariableScope(
                new Dictionary<string, string?>
                {
                    [DesktopParentProcessLifetime.ProcessIdEnvironmentVariable] =
                        parent.Id.ToString(CultureInfo.InvariantCulture),
                    [DesktopParentProcessLifetime.ProcessStartedAtEnvironmentVariable] =
                        actualStartedAt.ToString(CultureInfo.InvariantCulture)
                });

            var exception = Assert.Throws<InvalidOperationException>(() =>
                DesktopParentProcessLifetime.FromEnvironment(
                    desktopHandshakeConfigured: true));

            Assert.Contains(
                "created before the API",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Terminate(parent);
        }
    }

    [Fact]
    public async Task ParentExitRequestsGracefulStopAndAcceptsApplicationStopped()
    {
        var parentExited = NewCompletionSource();
        var deadlineReached = NewCompletionSource();
        var requestedDeadline = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failFastCalled = false;
        using var hostLifetime = new TestHostApplicationLifetime();
        using var parentLifetime = new DesktopParentProcessLifetime(
            _ => parentExited.Task,
            TimeSpan.FromSeconds(3),
            (delay, cancellationToken) =>
            {
                requestedDeadline.TrySetResult(delay);
                return deadlineReached.Task.WaitAsync(cancellationToken);
            },
            _ => failFastCalled = true);

        var monitor = parentLifetime.MonitorAsync(hostLifetime);
        parentExited.SetResult();
        await hostLifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(3), await requestedDeadline.Task);

        hostLifetime.CompleteStopped();
        await monitor.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, hostLifetime.StopApplicationCalls);
        Assert.False(failFastCalled);
    }

    [Fact]
    public async Task ParentWaitFailureUsesTheSameFailClosedShutdownPath()
    {
        var deadlineReached = NewCompletionSource();
        var deadlineRequested = NewCompletionSource();
        using var hostLifetime = new TestHostApplicationLifetime();
        using var parentLifetime = new DesktopParentProcessLifetime(
            _ => Task.FromException(
                new InvalidOperationException("Synthetic parent wait failure.")),
            TimeSpan.FromSeconds(3),
            (_, _) =>
            {
                deadlineRequested.SetResult();
                return deadlineReached.Task;
            },
            message => throw new ExpectedFailFastException(message));

        var monitor = parentLifetime.MonitorAsync(hostLifetime);
        await deadlineRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        deadlineReached.SetResult();

        await Assert.ThrowsAsync<ExpectedFailFastException>(() => monitor);
        Assert.Equal(1, hostLifetime.StopApplicationCalls);
    }

    [Fact]
    public async Task NormalApplicationShutdownEndsParentMonitoringOnlyAfterApplicationStopped()
    {
        var deadlineRequested = false;
        var failFastCalled = false;
        using var hostLifetime = new TestHostApplicationLifetime();
        using var parentLifetime = new DesktopParentProcessLifetime(
            cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            TimeSpan.FromSeconds(3),
            (_, _) =>
            {
                deadlineRequested = true;
                return Task.CompletedTask;
            },
            _ => failFastCalled = true);

        var monitor = parentLifetime.MonitorAsync(hostLifetime);
        hostLifetime.StopApplication();
        await Task.Delay(20);
        Assert.False(monitor.IsCompleted);

        hostLifetime.CompleteStopped();
        await monitor.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, hostLifetime.StopApplicationCalls);
        Assert.False(deadlineRequested);
        Assert.False(failFastCalled);
    }

    [Fact]
    public async Task ParentExitDuringStuckStoppingStillStartsDeadline()
    {
        var parentExited = NewCompletionSource();
        var deadlineReached = NewCompletionSource();
        var deadlineRequested = NewCompletionSource();
        using var hostLifetime = new TestHostApplicationLifetime();
        using var parentLifetime = new DesktopParentProcessLifetime(
            _ => parentExited.Task,
            TimeSpan.FromSeconds(3),
            (_, _) =>
            {
                deadlineRequested.SetResult();
                return deadlineReached.Task;
            },
            message => throw new ExpectedFailFastException(message));

        var monitor = parentLifetime.MonitorAsync(hostLifetime);
        hostLifetime.StopApplication();
        parentExited.SetResult();
        await deadlineRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        deadlineReached.SetResult();

        await Assert.ThrowsAsync<ExpectedFailFastException>(() => monitor);
        Assert.Equal(2, hostLifetime.StopApplicationCalls);
    }

    [Fact]
    public async Task ParentExitDeadlineInvokesFailFastWhenApplicationNeverStops()
    {
        var parentExited = NewCompletionSource();
        var deadlineReached = NewCompletionSource();
        var deadlineRequested = NewCompletionSource();
        string? failFastMessage = null;
        using var hostLifetime = new TestHostApplicationLifetime();
        using var parentLifetime = new DesktopParentProcessLifetime(
            _ => parentExited.Task,
            TimeSpan.FromSeconds(3),
            (_, _) =>
            {
                deadlineRequested.SetResult();
                return deadlineReached.Task;
            },
            message =>
            {
                failFastMessage = message;
                throw new ExpectedFailFastException(message);
            });

        var monitor = parentLifetime.MonitorAsync(hostLifetime);
        parentExited.SetResult();
        await deadlineRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        deadlineReached.SetResult();

        var exception = await Assert.ThrowsAsync<ExpectedFailFastException>(
            () => monitor);
        Assert.Equal(failFastMessage, exception.Message);
        Assert.Contains("3 seconds", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, hostLifetime.StopApplicationCalls);
        Assert.False(hostLifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public void ParentExitShutdownDeadlineIsTenSeconds()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            DesktopParentProcessLifetime.ParentExitShutdownDeadline);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ParentExitShutdownDeadlineMustBePositiveAndFinite(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DesktopParentProcessLifetime(
                _ => Task.CompletedTask,
                seconds == -1 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds),
                static (_, _) => Task.CompletedTask,
                static _ => { }));
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Process StartLongRunningProcess()
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
               ?? throw new InvalidOperationException(
                   "Desktop parent identity helper did not start.");
    }

    private static void Terminate(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(milliseconds: 10_000))
            {
                throw new TimeoutException("The desktop parent lifetime test process did not stop within 10 seconds.");
            }
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        internal TaskCompletionSource StopRequested { get; } =
            NewCompletionSource();

        internal int StopApplicationCalls { get; private set; }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopApplicationCalls++;
            _stopping.Cancel();
            StopRequested.TrySetResult();
        }

        internal void CompleteStopped()
        {
            _stopped.Cancel();
        }

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private sealed class ExpectedFailFastException(string message)
        : Exception(message);

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _originalValues;

        internal EnvironmentVariableScope(
            IReadOnlyDictionary<string, string?> values)
        {
            _originalValues = values.Keys.ToDictionary(
                static name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var pair in values)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _originalValues)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopParentProcessEnvironmentTestGroup
{
    public const string Name = "Desktop parent process environment";
}
