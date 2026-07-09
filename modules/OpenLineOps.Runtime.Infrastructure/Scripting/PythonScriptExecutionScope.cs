using System.Diagnostics;
using OpenLineOps.Runtime.Application.Commands;
using Python.Runtime;
using PythonScript.Exceptions;
using PythonScript.Runtime;

namespace OpenLineOps.Runtime.Infrastructure.Scripting;

public static class PythonScriptExecutionScope
{
    public static RuntimeCommandExecutionResult Execute(
        PythonScriptExecutionScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.ScriptLanguage, "Python", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeCommandExecutionResult.Rejected(
                $"Script language '{request.ScriptLanguage}' is not supported by the PythonScript runtime executor.");
        }

        if (string.IsNullOrWhiteSpace(request.ScriptSourceCode))
        {
            return RuntimeCommandExecutionResult.Rejected(
                "Python source code is required for runtime execution.");
        }

        ConfigurePythonRuntime();

        try
        {
            using var session = new PythonRuntimeSession();
            var payload = session.WithGil(() => ExecuteInScope(session, request));

            return RuntimeCommandExecutionResult.Completed(payload);
        }
        catch (PythonScriptExecutionException exception)
        {
            return RuntimeCommandExecutionResult.Failed(exception.PythonTraceback ?? exception.Message);
        }
        catch (PythonException exception)
        {
            return RuntimeCommandExecutionResult.Failed(exception.Message);
        }
        catch (Exception exception)
        {
            return RuntimeCommandExecutionResult.Failed(exception.Message);
        }
    }

    private static string? ExecuteInScope(
        PythonRuntimeSession session,
        PythonScriptExecutionScopeRequest request)
    {
        using var scope = session.CreateChildScope();
        scope.Set("input_payload", request.InputPayload);
        scope.Set("script_version", request.ScriptVersion);
        scope.Set("session_id", request.SessionId);
        scope.Set("station_id", request.StationId);
        scope.Set("configuration_snapshot_id", request.ConfigurationSnapshotId);
        scope.Set("node_id", request.NodeId);
        scope.Set("command_id", request.CommandId);

        scope.Exec("""
            import io
            __openlineops_stdout = io.StringIO()
            __openlineops_stderr = io.StringIO()
            __openlineops_old_stdout = sys.stdout
            __openlineops_old_stderr = sys.stderr
            sys.stdout = __openlineops_stdout
            sys.stderr = __openlineops_stderr
            """);

        try
        {
            scope.Exec(request.ScriptSourceCode);
        }
        finally
        {
            RestorePythonStreams(scope);
        }

        if (!scope.HasAttr("result"))
        {
            return null;
        }

        scope.Exec("""
            import json
            __openlineops_payload = json.dumps(
                result,
                ensure_ascii=False,
                default=str,
                separators=(',', ':'))
            """);

        using var payload = scope.GetAttr("__openlineops_payload");
        return payload.As<string>();
    }

    private static void RestorePythonStreams(PyModule scope)
    {
        try
        {
            scope.Exec("""
                sys.stdout = __openlineops_old_stdout
                sys.stderr = __openlineops_old_stderr
                """);
        }
        catch (PythonException)
        {
            // The command is already terminal; stream restoration should not hide the script failure.
        }
    }

    private static void ConfigurePythonRuntime()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PYTHONNET_PYDLL")))
        {
            return;
        }

        var pythonDllPath = TryDiscoverPythonDllPath();
        if (!string.IsNullOrWhiteSpace(pythonDllPath))
        {
            Environment.SetEnvironmentVariable("PYTHONNET_PYDLL", pythonDllPath);
        }
    }

    private static string? TryDiscoverPythonDllPath()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "-c \"import pathlib, sysconfig; print(pathlib.Path(sysconfig.get_config_var('BINDIR') or '').joinpath(sysconfig.get_config_var('LDLIBRARY') or '').resolve())\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            if (!process.WaitForExit(milliseconds: 2_000) || process.ExitCode != 0)
            {
                return null;
            }

            var candidate = process.StandardOutput.ReadLine();
            return !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)
                ? candidate
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
