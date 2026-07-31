using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenLineOps.ProcessIsolation;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.StationRuntime.TestHelper;

public sealed class StationRuntimeTestHelperMarker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "supervise-operation", StringComparison.Ordinal))
        {
            return await SuperviseOperationAsync(args.AsSpan(1).ToArray());
        }

        if (args.Length > 0 && string.Equals(args[0], "execute-nested-operation", StringComparison.Ordinal))
        {
            return await ExecuteNestedOperationAsync(args.AsSpan(1).ToArray());
        }

        if (args.Length == 0 || !string.Equals(args[0], "execute-operation", StringComparison.Ordinal))
        {
            return 64;
        }

        var options = ParseOptions(args.AsSpan(1));
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(options["request-file"]));
        var inputs = ProductionContextDocument.Read(
            request.RootElement.GetProperty("inputs"));
        var mode = inputs["mode"].CanonicalValue;
        if (!string.Equals(mode, "spawn-child", StringComparison.Ordinal))
        {
            return 65;
        }

        var pidFile = inputs["pidFile"].CanonicalValue;
        if (inputs.TryGetValue("runtimePidFile", out var runtimePidFileValue))
        {
            var runtimePidFile = runtimePidFileValue.CanonicalValue;
            await File.WriteAllTextAsync(
                runtimePidFile,
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("127.0.0.1");
        var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Child process did not start.");
        await File.WriteAllTextAsync(
            pidFile,
            child.Id.ToString(CultureInfo.InvariantCulture));
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> SuperviseOperationAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var options = ParseOptions(args);
        var pidFile = options["pid-file"];
        var runtimePidFile = options["runtime-pid-file"];
        var workDirectory = Path.GetFullPath(options["work-directory"]);
        Directory.CreateDirectory(workDirectory);
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Supervisor executable path is unavailable.");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyEnvironment(environment, "SystemRoot");
        CopyEnvironment(environment, "WINDIR");
        CopyEnvironment(environment, "PATH");
        environment["TEMP"] = workDirectory;
        environment["TMP"] = workDirectory;
        using var process = new IsolatedProcessLauncher().Launch(
            new IsolatedProcessStartRequest(
                Path.GetFullPath(executable),
                [
                    "execute-nested-operation",
                    "--pid-file",
                    pidFile,
                    "--runtime-pid-file",
                    runtimePidFile,
                    "--work-directory",
                    workDirectory
                ],
                workDirectory,
                environment,
                new WindowsProcessLimits(
                    ActiveProcessLimit: 4,
                    ProcessMemoryLimitBytes: 512L * 1024 * 1024,
                    JobMemoryLimitBytes: 1024L * 1024 * 1024,
                    CpuTimeLimit: TimeSpan.FromMinutes(5))));
        process.StandardInput.Dispose();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> ExecuteNestedOperationAsync(string[] args)
    {
        var options = ParseOptions(args);
        var pidFile = options["pid-file"];
        var runtimePidFile = options["runtime-pid-file"];
        var workDirectory = Path.GetFullPath(options["work-directory"]);
        await File.WriteAllTextAsync(
            runtimePidFile,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyEnvironment(environment, "SystemRoot");
        CopyEnvironment(environment, "WINDIR");
        environment["TEMP"] = workDirectory;
        environment["TMP"] = workDirectory;
        using var child = new IsolatedProcessLauncher().Launch(
            new IsolatedProcessStartRequest(
                Path.Combine(Environment.SystemDirectory, "ping.exe"),
                ["-t", "127.0.0.1"],
                workDirectory,
                environment,
                new WindowsProcessLimits(
                    ActiveProcessLimit: 2,
                    ProcessMemoryLimitBytes: 128L * 1024 * 1024,
                    JobMemoryLimitBytes: 256L * 1024 * 1024,
                    CpuTimeLimit: TimeSpan.FromMinutes(5))));
        child.StandardInput.Dispose();
        await File.WriteAllTextAsync(
            pidFile,
            child.Id.ToString(CultureInfo.InvariantCulture));
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static void CopyEnvironment(Dictionary<string, string> environment, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
        {
            environment[name] = value;
        }
    }

    private static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Station runtime helper arguments are invalid.");
            }

            result.Add(args[index][2..], args[index + 1]);
        }

        return result;
    }
}
