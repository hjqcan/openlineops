using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.ProcessTreeHost;

internal static class Program
{
    private const int UsageExitCode = 64;
    private const int OwnerExitedExitCode = 70;
    private static readonly TimeSpan OwnerLossDrainTimeout =
        TimeSpan.FromSeconds(5);

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "OpenLineOps Process Tree Host requires Windows.");
            return UsageExitCode;
        }

        if (args is not [
                var ownerProcessIdText,
                var ownerStartedAtUnixMillisecondsText,
                var executablePath,
                var workingDirectory,
                .. var childArguments]
            || !int.TryParse(
                ownerProcessIdText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ownerProcessId)
            || ownerProcessId <= 0
            || ownerProcessId == Environment.ProcessId)
        {
            Console.Error.WriteLine(
                "Usage: OpenLineOps.ProcessTreeHost <owner-process-id> "
                + "<owner-started-at-unix-ms> "
                + "<absolute-executable> "
                + "<absolute-working-directory> [arguments...]");
            return UsageExitCode;
        }
        if (!long.TryParse(
                ownerStartedAtUnixMillisecondsText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ownerStartedAtUnixMilliseconds)
            || ownerStartedAtUnixMilliseconds <= 0
            || !string.Equals(
                ownerStartedAtUnixMillisecondsText,
                ownerStartedAtUnixMilliseconds.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Process Tree Host owner creation identity must be a "
                + "positive Unix millisecond value.");
            return UsageExitCode;
        }
        try
        {
            _ = DateTimeOffset.FromUnixTimeMilliseconds(
                ownerStartedAtUnixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            Console.Error.WriteLine(
                "Process Tree Host owner creation identity is outside the "
                + "supported Unix timestamp range.");
            return UsageExitCode;
        }

        Process ownerProcess;
        try
        {
            ownerProcess = RequireRunningOwner(
                ownerProcessId,
                ownerStartedAtUnixMilliseconds);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return UsageExitCode;
        }
        using (ownerProcess)
        {
            return await RunHostedProcessAsync(
                    ownerProcess,
                    executablePath,
                    workingDirectory,
                    childArguments)
                .ConfigureAwait(false);
        }
    }

    private static async Task<int> RunHostedProcessAsync(
        Process ownerProcess,
        string executablePath,
        string workingDirectory,
        string[] childArguments)
    {
        executablePath = RequireCanonicalFile(
            executablePath,
            "hosted executable");
        workingDirectory = RequireCanonicalDirectory(
            workingDirectory,
            "hosted working directory");
        using var process = new WindowsProcessLauncher().Launch(
            new IsolatedProcessStartRequest(
                executablePath,
                childArguments,
                workingDirectory,
                ReadEnvironment(),
                Limits: null));
        Console.Error.WriteLine(
            $"OPENLINEOPS_PROCESS_TREE_ROOT {process.Id} "
            + $"{process.StartedAtUnixMilliseconds}");
        await Console.Error.FlushAsync();

        using var inputCancellation = new CancellationTokenSource();
        var standardInput = ForwardStandardInputAsync(
            process.StandardInput,
            inputCancellation.Token);
        var standardOutput = process.StandardOutput.CopyToAsync(
            Console.OpenStandardOutput());
        var standardError = process.StandardError.CopyToAsync(
            Console.OpenStandardError());
        var outputCompletion = Task.WhenAll(standardOutput, standardError);
        var outputFailure = ObserveFirstFailure(
            standardOutput,
            standardError);
        var hostedProcessExit = process.WaitForExitAsync();
        var hostedProcessTreeExit = process.WaitForProcessTreeExitAsync();
        var hostedLifecycleExit = Task.WhenAll(
            hostedProcessExit,
            hostedProcessTreeExit);
        using var ownerExitCancellation = new CancellationTokenSource();
        var ownerProcessExit = ownerProcess.WaitForExitAsync(
            ownerExitCancellation.Token);
        var completed = await Task.WhenAny(
                hostedLifecycleExit,
                ownerProcessExit,
                outputFailure)
            .ConfigureAwait(false);
        var ownerExitedFirst = completed == ownerProcessExit
                               && !hostedLifecycleExit.IsCompleted;
        var outputFailedFirst = completed == outputFailure
                                && !hostedLifecycleExit.IsCompleted;
        if (ownerExitedFirst || outputFailedFirst)
        {
            process.TerminateProcessTree();
            try
            {
                await hostedLifecycleExit
                    .WaitAsync(OwnerLossDrainTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ObserveLateFailure(hostedLifecycleExit);
                inputCancellation.Cancel();
                process.StandardInput.Dispose();
                ObserveLateFailure(standardInput);
                ObserveLateFailure(outputCompletion);
                ObserveLateFailure(ownerProcessExit);
                if (outputFailedFirst)
                {
                    ExceptionDispatchInfo.Capture(
                        await outputFailure.ConfigureAwait(false)).Throw();
                }
                return OwnerExitedExitCode;
            }
        }
        else
        {
            await hostedLifecycleExit.ConfigureAwait(false);
            await ownerExitCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await ownerProcessExit.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The hosted tree completed while its owner remained alive.
            }
        }
        if (outputFailedFirst)
        {
            ObserveLateFailure(outputCompletion);
            inputCancellation.Cancel();
            process.StandardInput.Dispose();
            await ObserveStandardInputCompletionAsync(standardInput)
                .ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(
                await outputFailure.ConfigureAwait(false)).Throw();
        }
        await ObserveOutputCompletionAsync(
                standardOutput,
                standardError,
                ownerExitedFirst)
            .ConfigureAwait(false);
        inputCancellation.Cancel();
        process.StandardInput.Dispose();
        await ObserveStandardInputCompletionAsync(standardInput)
            .ConfigureAwait(false);
        if (ownerExitedFirst)
        {
            await ownerProcessExit.ConfigureAwait(false);
        }
        return ownerExitedFirst
            ? OwnerExitedExitCode
            : process.ExitCode;
    }

    private static Process RequireRunningOwner(
        int processId,
        long expectedStartedAtUnixMilliseconds)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "Process Tree Host owner is not running.",
                nameof(processId),
                exception);
        }
        try
        {
            if (process.HasExited)
            {
                throw new ArgumentException(
                    "Process Tree Host owner has already exited.",
                    nameof(processId));
            }
            var actualStartedAtUnixMilliseconds = new DateTimeOffset(
                    process.StartTime.ToUniversalTime())
                .ToUnixTimeMilliseconds();
            if (actualStartedAtUnixMilliseconds
                != expectedStartedAtUnixMilliseconds)
            {
                throw new ArgumentException(
                    "Process Tree Host owner creation identity does not match.",
                    nameof(expectedStartedAtUnixMilliseconds));
            }
            using var currentProcess = Process.GetCurrentProcess();
            if (process.StartTime.ToUniversalTime()
                >= currentProcess.StartTime.ToUniversalTime())
            {
                throw new ArgumentException(
                    "Process Tree Host owner must have been created before the Host.",
                    nameof(processId));
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task ForwardStandardInputAsync(
        Stream destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await Console.OpenStandardInput()
                .CopyToAsync(destination, cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException
            or IOException
            or ObjectDisposedException)
        {
            // The hosted process or its caller closed the redirected stream.
        }
        finally
        {
            destination.Dispose();
        }
    }

    private static async Task ObserveStandardInputCompletionAsync(Task forwarding)
    {
        try
        {
            await forwarding
                .WaitAsync(TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Some console hosts do not cancel an outstanding redirected read.
            // This background operation cannot retain the Process Tree Host.
            ObserveLateFailure(forwarding);
        }
    }

    private static async Task ObserveOutputCompletionAsync(
        Task standardOutput,
        Task standardError,
        bool ownerExitedFirst)
    {
        var outputCompletion = Task.WhenAll(standardOutput, standardError);
        try
        {
            await outputCompletion
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            ownerExitedFirst
            && exception is IOException or ObjectDisposedException)
        {
            // The owner can close its redirected output while the Job is
            // terminating. The process-tree drain above remains authoritative.
        }
        catch (TimeoutException)
        {
            // A non-reading owner cannot retain this Host after the contained
            // process tree has fully drained.
            ObserveLateFailure(outputCompletion);
        }
    }

    private static void ObserveLateFailure(Task operation)
    {
        _ = operation.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
            | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static Task<Exception> ObserveFirstFailure(params Task[] operations)
    {
        var failure = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var operation in operations)
        {
            _ = operation.ContinueWith(
                static (completed, state) =>
                {
                    var source = (TaskCompletionSource<Exception>)state!;
                    source.TrySetResult(completed.Exception!.Flatten());
                },
                failure,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        return failure.Task;
    }

    private static Dictionary<string, string> ReadEnvironment()
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key
                && entry.Value is string value
                && key.Length > 0
                && key[0] != '=')
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string RequireCanonicalFile(string path, string description)
    {
        var fullPath = RequireCanonicalAbsolutePath(path, description);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException(
                $"{description} was not found.",
                fullPath);
    }

    private static string RequireCanonicalDirectory(
        string path,
        string description)
    {
        var fullPath = RequireCanonicalAbsolutePath(path, description);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new DirectoryNotFoundException(
                $"{description} was not found: {fullPath}");
    }

    private static string RequireCanonicalAbsolutePath(
        string path,
        string description)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                $"{description} must be an absolute path.",
                nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        return string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new ArgumentException(
                $"{description} must already be canonical.",
                nameof(path));
    }
}
