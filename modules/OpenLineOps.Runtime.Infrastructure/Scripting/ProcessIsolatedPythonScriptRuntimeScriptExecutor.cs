using System.Diagnostics;
using System.Text.Json;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Scripting;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public sealed class ProcessIsolatedPythonScriptRuntimeScriptExecutor : IRuntimeScriptExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PythonScriptRuntimeOptions _options;

    public ProcessIsolatedPythonScriptRuntimeScriptExecutor(PythonScriptRuntimeOptions options)
    {
        _options = options;
    }

    public async ValueTask<RuntimeCommandExecutionResult> ExecuteAsync(
        RuntimeScriptExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var policyFailure = PythonScriptWorkerStartInfoBuilder.ValidateSandboxPolicy(_options);
        if (policyFailure is not null)
        {
            return RuntimeCommandExecutionResult.Rejected(policyFailure);
        }

        using var timeoutCancellation = CreateTimeoutCancellationTokenSource(request.CommandContext.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation?.Token ?? CancellationToken.None);
        var linkedToken = linkedCancellation.Token;

        using var process = new Process
        {
            StartInfo = PythonScriptWorkerStartInfoBuilder.Build(_options),
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                return RuntimeCommandExecutionResult.Failed("Python script worker process did not start.");
            }

            var workerRequest = PythonScriptExecutionScopeRequest.FromRuntimeRequest(request);
            var requestJson = JsonSerializer.Serialize(workerRequest, JsonOptions);

            await process.StandardInput
                .WriteAsync(requestJson.AsMemory(), linkedToken)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(linkedToken).ConfigureAwait(false);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedToken);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedToken);

            await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return RuntimeCommandExecutionResult.Failed(
                    NormalizeWorkerFailure(stderr, stdout, $"Python script worker exited with code {process.ExitCode}."));
            }

            return DeserializeWorkerResult(stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            return RuntimeCommandExecutionResult.Canceled("Python script worker execution was canceled.");
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            return RuntimeCommandExecutionResult.TimedOut("Python script worker execution timed out.");
        }
        catch (Exception exception)
        {
            KillProcessTree(process);
            return RuntimeCommandExecutionResult.Failed(exception.Message);
        }
    }

    private static RuntimeCommandExecutionResult DeserializeWorkerResult(
        string stdout,
        string stderr)
    {
        try
        {
            var workerResult = JsonSerializer.Deserialize<PythonScriptWorkerExecutionResult>(stdout, JsonOptions);
            if (workerResult is null)
            {
                return RuntimeCommandExecutionResult.Failed(
                    NormalizeWorkerFailure(stderr, stdout, "Python script worker returned an empty response."));
            }

            return workerResult.ToRuntimeResult();
        }
        catch (JsonException exception)
        {
            return RuntimeCommandExecutionResult.Failed(
                NormalizeWorkerFailure(
                    stderr,
                    stdout,
                    $"Python script worker returned invalid JSON: {exception.Message}"));
        }
    }

    private static CancellationTokenSource? CreateTimeoutCancellationTokenSource(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return null;
        }

        return new CancellationTokenSource(timeout);
    }

    private static string NormalizeWorkerFailure(
        string stderr,
        string stdout,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            return stderr.Trim();
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            return stdout.Trim();
        }

        return fallback;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
