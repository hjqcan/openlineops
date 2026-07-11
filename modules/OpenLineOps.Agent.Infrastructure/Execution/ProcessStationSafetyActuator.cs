using System.Diagnostics;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Execution;

public sealed record ProcessStationSafetyOptions(
    string ExecutablePath,
    string WorkingDirectory,
    TimeSpan Timeout);

public sealed class ProcessStationSafetyActuator : IStationSafetyActuator
{
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly TimeSpan _timeout;

    public ProcessStationSafetyActuator(ProcessStationSafetyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Station safety timeout must be positive.");
        }

        _executablePath = Path.GetFullPath(options.ExecutablePath);
        _workingDirectory = Path.GetFullPath(options.WorkingDirectory);
        _timeout = options.Timeout;
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException("Station safety executable does not exist.", _executablePath);
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
            arguments.Add(new SafetyArgument("operation-run-id", request.OperationRunId));
        }

        return ExecuteAsync("safe-stop", arguments, cancellationToken);
    }

    private async ValueTask<StationSafetyExecutionResult> ExecuteAsync(
        string command,
        IReadOnlyCollection<SafetyArgument> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment.Clear();
        CopyEnvironment(startInfo, "SystemRoot");
        CopyEnvironment(startInfo, "WINDIR");
        CopyEnvironment(startInfo, "PATH");
        startInfo.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add($"--{argument.Name}");
            startInfo.ArgumentList.Add(argument.Value);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            return new StationSafetyExecutionResult(
                false,
                "Agent.SafetyStartFailed",
                $"Station safety command '{command}' did not start.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return new StationSafetyExecutionResult(
                false,
                "Agent.SafetyTimedOut",
                $"Station safety command '{command}' exceeded {_timeout}.");
        }

        return process.ExitCode == 0
            ? new StationSafetyExecutionResult(true, null, null)
            : new StationSafetyExecutionResult(
                false,
                "Agent.SafetyFailed",
                $"Station safety command '{command}' exited with code {process.ExitCode}.");
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            startInfo.Environment[name] = value;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record SafetyArgument(string Name, string Value);
}
