using System.Diagnostics;
using System.Globalization;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Domain.Nodes;
using PythonScript.Runtime;
using PythonScript.SyntaxStaticCheck;

namespace OpenLineOps.Processes.Infrastructure.Scripting;

public sealed class PythonScriptDefinitionValidator : IProcessScriptDefinitionValidator
{
    public ValueTask<ProcessScriptValidationReport> ValidateAsync(
        ProcessNode node,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();

        if (!node.IsPythonScript)
        {
            return ValueTask.FromResult(ProcessScriptValidationReport.Valid);
        }

        if (!string.Equals(node.ScriptLanguage, "Python", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Invalid(new ProcessScriptValidationIssue(
                "PYTHON_LANGUAGE_UNSUPPORTED",
                $"Script language '{node.ScriptLanguage}' is not supported by the PythonScript validator.",
                Line: 0,
                Column: 0)));
        }

        if (string.IsNullOrWhiteSpace(node.ScriptSourceCode))
        {
            return ValueTask.FromResult(Invalid(new ProcessScriptValidationIssue(
                "PYTHON_SOURCE_MISSING",
                "Python source code is required for syntax validation.",
                Line: 0,
                Column: 0)));
        }

        ConfigurePythonRuntime();
        if (!PythonHost.TryEnsureInitialized(out var initializationError))
        {
            return ValueTask.FromResult(Invalid(new ProcessScriptValidationIssue(
                "PYTHON_RUNTIME_UNAVAILABLE",
                $"Python runtime could not be initialized: {initializationError}",
                Line: 0,
                Column: 0)));
        }

        try
        {
            var checker = new PythonSyntaxChecker();
            var errors = checker
                .AnalyzeSyntaxErrors(node.ScriptSourceCode, $"{node.Id.Value}.py")
                .Select(error => new ProcessScriptValidationIssue(
                    NormalizeCode(error.ErrorCode),
                    error.Message,
                    error.Line,
                    error.Column))
                .ToArray();

            return ValueTask.FromResult(errors.Length == 0
                ? ProcessScriptValidationReport.Valid
                : new ProcessScriptValidationReport(errors));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Invalid(new ProcessScriptValidationIssue(
                "PYTHON_VALIDATION_FAILED",
                $"Python source validation failed: {exception.Message}",
                Line: 0,
                Column: 0)));
        }
    }

    private static ProcessScriptValidationReport Invalid(ProcessScriptValidationIssue issue)
    {
        return new ProcessScriptValidationReport([issue]);
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

    private static string NormalizeCode(string? code)
    {
        return string.IsNullOrWhiteSpace(code)
            ? "PYTHON_SYNTAX_ERROR"
            : code.Trim().ToUpper(CultureInfo.InvariantCulture);
    }
}
