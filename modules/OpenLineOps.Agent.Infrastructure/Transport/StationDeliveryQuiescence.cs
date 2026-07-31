using System.Diagnostics;
using RabbitMQ.Client;

namespace OpenLineOps.Agent.Infrastructure.Transport;

internal sealed class StationDeliveryQuiescence
{
    private static readonly TimeSpan MaximumDrainDuration = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private TaskCompletionSource? _drained;
    private int _activeDeliveries;
    private bool _accepting = true;

    public Task ExecuteAsync(
        Func<Task> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_accepting || cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            _activeDeliveries++;
        }

        return ExecuteTrackedAsync(handler, cancellationToken);
    }

    public void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
        }
    }

    public async Task StopAcceptingAndWaitAsync()
    {
        StopAccepting();
        Task drained;
        lock (_gate)
        {
            if (_activeDeliveries == 0)
            {
                return;
            }

            _drained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _drained.Task;
        }

        try
        {
            await drained.WaitAsync(MaximumDrainDuration).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new StationDeliveryDrainTimeoutException(exception);
        }
    }

    private async Task ExecuteTrackedAsync(
        Func<Task> handler,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await handler().ConfigureAwait(false);
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (_gate)
            {
                _activeDeliveries--;
                if (_activeDeliveries == 0 && !_accepting)
                {
                    drained = _drained;
                }
            }

            drained?.TrySetResult();
        }
    }
}

public sealed class StationDeliveryDrainTimeoutException(Exception innerException) :
    TimeoutException(
        "Station delivery handlers did not quiesce before the shutdown deadline.",
        innerException);

internal static class RabbitMqTransportShutdown
{
    private const ushort NormalReplyCode = 200;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(5);

    public static TimeSpan MaximumTotalSessionShutdown => TimeSpan.FromSeconds(30);

    public static async ValueTask CloseChannelAsync(IChannel? channel)
    {
        if (channel is null)
        {
            return;
        }

        var elapsed = Stopwatch.StartNew();
        var failures = new List<Exception>();
        try
        {
            if (channel.IsOpen)
            {
                var remaining = Remaining(elapsed);
                using var close = new CancellationTokenSource(remaining);
                await channel
                    .CloseAsync(
                        NormalReplyCode,
                        "OpenLineOps Agent shutdown",
                        abort: true,
                        close.Token)
                    .WaitAsync(remaining)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsExpectedCloseFailure(exception))
        {
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await CaptureDisposeFailureAsync(
                failures,
                () => channel.DisposeAsync(),
                elapsed)
            .ConfigureAwait(false);
        ThrowIfFailed(failures);
    }

    public static async ValueTask CloseConnectionAsync(IConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        var elapsed = Stopwatch.StartNew();
        var failures = new List<Exception>();
        try
        {
            if (connection.IsOpen)
            {
                var remaining = Remaining(elapsed);
                using var close = new CancellationTokenSource(remaining);
                await connection
                    .CloseAsync(
                        NormalReplyCode,
                        "OpenLineOps Agent shutdown",
                        remaining,
                        abort: true,
                        close.Token)
                    .WaitAsync(remaining)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsExpectedCloseFailure(exception))
        {
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await CaptureDisposeFailureAsync(
                failures,
                () => connection.DisposeAsync(),
                elapsed)
            .ConfigureAwait(false);
        ThrowIfFailed(failures);
    }

    public static async ValueTask CancelConsumerAsync(
        IChannel? channel,
        string? consumerTag)
    {
        if (channel is null
            || string.IsNullOrEmpty(consumerTag)
            || !channel.IsOpen)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(MaximumDuration);
        try
        {
            await channel.BasicCancelAsync(
                    consumerTag,
                    noWait: false,
                    cancellation.Token)
                .WaitAsync(MaximumDuration)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedCloseFailure(exception))
        {
        }
    }

    private static async ValueTask CaptureDisposeFailureAsync(
        List<Exception> failures,
        Func<ValueTask> dispose,
        Stopwatch elapsed)
    {
        try
        {
            var disposal = dispose().AsTask();
            await disposal.WaitAsync(Remaining(elapsed)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowIfFailed(List<Exception> failures)
    {
        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    private static bool IsExpectedCloseFailure(Exception exception) =>
        exception is OperationCanceledException
            or IOException
            or TimeoutException
            or RabbitMQ.Client.Exceptions.AlreadyClosedException
            or RabbitMQ.Client.Exceptions.OperationInterruptedException;

    private static TimeSpan Remaining(Stopwatch elapsed)
    {
        var remaining = MaximumDuration - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "RabbitMQ resource shutdown exceeded its bounded deadline.");
        }

        return remaining;
    }
}
