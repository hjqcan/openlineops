using System.ComponentModel;
using System.Runtime.ExceptionServices;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.Agent.Infrastructure.Execution;

public sealed record ProcessStationSafetyOptions(
    string ExecutablePath,
    string WorkingDirectory,
    TimeSpan Timeout);

public sealed class ProcessStationSafetyActuator : IStationSafetyActuator
{
    private static readonly TimeSpan MaximumCancellationTimeout =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1L);
    private static readonly TimeSpan TerminationDrainTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly WindowsProcessLimits SafetyProcessLimits = new(
        ActiveProcessLimit: 16,
        ProcessMemoryLimitBytes: 512L * 1024 * 1024,
        JobMemoryLimitBytes: 2L * 1024 * 1024 * 1024,
        CpuTimeLimit: TimeSpan.FromHours(1));
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly TimeSpan _timeout;
    private readonly WindowsProcessLauncher _processLauncher;

    public ProcessStationSafetyActuator(
        ProcessStationSafetyOptions options,
        WindowsProcessLauncher? processLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        if (options.Timeout <= TimeSpan.Zero
            || options.Timeout > MaximumCancellationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Station safety timeout must be positive and no greater than "
                + $"{MaximumCancellationTimeout}.");
        }

        _executablePath = Path.GetFullPath(options.ExecutablePath);
        _workingDirectory = Path.GetFullPath(options.WorkingDirectory);
        _timeout = options.Timeout;
        _processLauncher = processLauncher ?? new WindowsProcessLauncher();
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException(
                "Station safety executable does not exist.",
                _executablePath);
        }

        Directory.CreateDirectory(_workingDirectory);
    }

    public ValueTask<StationSafetyExecutionResult> EmergencyStopAsync(
        EmergencyStopRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            "emergency-stop",
            [
                new("station", request.StationId),
                new("request-id", request.MessageId.ToString("D")),
                new("idempotency-key", request.IdempotencyKey),
                new("reason", request.Reason),
                new("requested-by", request.RequestedBy)
            ],
            cancellationToken);
    }

    public ValueTask<StationSafetyExecutionResult> SafeStopAsync(
        StationSafeStopRequested request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<SafetyArgument>
        {
            new("station", request.StationId),
            new("station-system", request.StationSystemId),
            new("request-id", request.MessageId.ToString("D")),
            new("run-id", request.ProductionRunId.ToString("D")),
            new("idempotency-key", request.IdempotencyKey),
            new("reason", request.Reason),
            new("requested-by", request.ActorId)
        };
        if (request.OperationRunId is not null)
        {
            arguments.Add(new SafetyArgument(
                "operation-run-id",
                request.OperationRunId));
        }

        return ExecuteAsync("safe-stop", arguments, cancellationToken);
    }

    private async ValueTask<StationSafetyExecutionResult> ExecuteAsync(
        string command,
        IReadOnlyCollection<SafetyArgument> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WindowsIsolatedProcess process;
        try
        {
            process = _processLauncher.Launch(
                new IsolatedProcessStartRequest(
                    _executablePath,
                    CreateArguments(command, arguments),
                    _workingDirectory,
                    CreateEnvironment(),
                    SafetyProcessLimits));
        }
        catch (Exception exception) when (IsProcessFailure(exception))
        {
            return Failure(
                "Agent.SafetyStartFailed",
                $"Station safety command '{command}' did not start.");
        }

        Task processTreeLifecycle = Task.CompletedTask;
        Task observationLifecycle = Task.CompletedTask;
        StationSafetyExecutionResult? result = null;
        OperationCanceledException? cancellation = null;
        try
        {
            processTreeLifecycle = Task.WhenAll(
                process.WaitForExitAsync(CancellationToken.None),
                process.WaitForProcessTreeExitAsync(CancellationToken.None));
            observationLifecycle = processTreeLifecycle;
            process.StandardInput.Dispose();
            observationLifecycle = Task.WhenAll(
                observationLifecycle,
                process.StandardOutput.CopyToAsync(
                    Stream.Null,
                    CancellationToken.None));
            observationLifecycle = Task.WhenAll(
                observationLifecycle,
                process.StandardError.CopyToAsync(
                    Stream.Null,
                    CancellationToken.None));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                await observationLifecycle
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
                result = process.ExitCode == 0
                    ? new StationSafetyExecutionResult(true, null, null)
                    : Failure(
                        "Agent.SafetyFailed",
                        $"Station safety command '{command}' exited with code "
                        + $"{process.ExitCode}.");
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                cancellation = exception;
                if (!await TryTerminateAndDrainAsync(process)
                    .ConfigureAwait(false))
                {
                    result = TerminationFailure(command);
                }
            }
            catch (OperationCanceledException)
            {
                result = await TryTerminateAndDrainAsync(process)
                    .ConfigureAwait(false)
                    ? Failure(
                        "Agent.SafetyTimedOut",
                        $"Station safety command '{command}' exceeded {_timeout}.")
                    : TerminationFailure(command);
            }
            catch (Exception exception) when (IsProcessFailure(exception))
            {
                result = await TryTerminateAndDrainAsync(process)
                    .ConfigureAwait(false)
                    ? Failure(
                        "Agent.SafetyExecutionFailed",
                        $"Station safety command '{command}' could not be observed safely.")
                    : TerminationFailure(command);
            }
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch (Exception exception) when (IsProcessFailure(exception))
            {
                result = TerminationFailure(command);
            }

            await ObserveAfterDisposalAsync(
                    observationLifecycle,
                    processTreeLifecycle)
                .ConfigureAwait(false);
        }

        if (cancellation is not null
            && !string.Equals(
                result?.FailureCode,
                "Agent.SafetyTerminationFailed",
                StringComparison.Ordinal))
        {
            ExceptionDispatchInfo.Capture(cancellation).Throw();
        }

        return result ?? TerminationFailure(command);
    }

    private static List<string> CreateArguments(
        string command,
        IEnumerable<SafetyArgument> arguments)
    {
        var result = new List<string> { command };
        foreach (var argument in arguments)
        {
            result.Add($"--{argument.Name}");
            result.Add(argument.Value);
        }

        return result;
    }

    private static Dictionary<string, string> CreateEnvironment()
    {
        var environment = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        CopyEnvironment(environment, "SystemRoot");
        CopyEnvironment(environment, "WINDIR");
        CopyEnvironment(environment, "PATH");
        return environment;
    }

    private static void CopyEnvironment(
        Dictionary<string, string> environment,
        string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            environment[name] = value;
        }
    }

    private static async Task<bool> TryTerminateAndDrainAsync(
        WindowsIsolatedProcess process)
    {
        try
        {
            process.TerminateProcessTree();
            using var deadline = new CancellationTokenSource(
                TerminationDrainTimeout);
            await Task.WhenAll(
                    process.WaitForExitAsync(CancellationToken.None),
                    process.WaitForProcessTreeExitAsync(CancellationToken.None))
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            return process.ActiveProcessCount == 0;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
            || IsProcessFailure(exception))
        {
            return false;
        }
    }

    private static async Task ObserveAfterDisposalAsync(
        Task observationLifecycle,
        Task processTreeLifecycle)
    {
        var completion = Task.WhenAll(
            observationLifecycle,
            processTreeLifecycle);
        try
        {
            await completion
                .WaitAsync(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = completion.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception) when (IsProcessFailure(exception))
        {
        }
    }

    private static bool IsProcessFailure(Exception exception) =>
        exception is Win32Exception
            or IOException
            or AggregateException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or ObjectDisposedException
            or PlatformNotSupportedException;

    private static StationSafetyExecutionResult TerminationFailure(
        string command) =>
        Failure(
            "Agent.SafetyTerminationFailed",
            $"Station safety command '{command}' process tree could not be "
            + "terminated within the safety deadline.");

    private static StationSafetyExecutionResult Failure(
        string code,
        string reason) =>
        new(false, code, reason);

    private sealed record SafetyArgument(string Name, string Value);
}
