using System.Diagnostics;
using System.Globalization;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.ProcessIsolation.TestHelper;

internal static class Program
{
    internal const int GracefulExitCode = 37;
    private const int UsageExitCode = 64;

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UsageExitCode;
        }

        return args switch
        {
            ["graceful-exit", var childProcessIdFile, var releaseFile] =>
                await SpawnChildAndExitAsync(childProcessIdFile, releaseFile),
            ["await-termination", var childProcessIdFile] =>
                await SpawnChildAndWaitAsync(childProcessIdFile),
            ["exit-with-unowned-child", var childProcessIdFile] =>
                await SpawnUnownedChildAndExitAsync(childProcessIdFile),
            [
                "own-process-tree-host",
                var hostExecutable,
                var hostedExecutable,
                var workingDirectory,
                var childProcessIdFile,
                var hostProcessIdFile,
                var hostedRootProcessIdFile
            ] => await OwnProcessTreeHostAsync(
                hostExecutable,
                hostedExecutable,
                workingDirectory,
                childProcessIdFile,
                hostProcessIdFile,
                hostedRootProcessIdFile),
            ["echo-standard-input"] =>
                await EchoStandardInputAsync(),
            _ => UsageExitCode
        };
    }

    private static async Task<int> EchoStandardInputAsync()
    {
        var input = await Console.In.ReadToEndAsync();
        await Console.Out.WriteAsync(input);
        return 0;
    }

    private static async Task<int> SpawnUnownedChildAndExitAsync(
        string childProcessIdFile)
    {
        using var child = StartLongRunningChild();
        await WriteProcessIdAsync(childProcessIdFile, child.Id);
        return 0;
    }

    private static async Task<int> OwnProcessTreeHostAsync(
        string hostExecutable,
        string hostedExecutable,
        string workingDirectory,
        string childProcessIdFile,
        string hostProcessIdFile,
        string hostedRootProcessIdFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(hostExecutable),
            WorkingDirectory = Path.GetDirectoryName(
                Path.GetFullPath(hostExecutable)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        using (var currentProcess = Process.GetCurrentProcess())
        {
            startInfo.ArgumentList.Add(
                new DateTimeOffset(
                        currentProcess.StartTime.ToUniversalTime())
                    .ToUnixTimeMilliseconds()
                    .ToString(CultureInfo.InvariantCulture));
        }
        startInfo.ArgumentList.Add(Path.GetFullPath(hostedExecutable));
        startInfo.ArgumentList.Add(Path.GetFullPath(workingDirectory));
        startInfo.ArgumentList.Add("exit-with-unowned-child");
        startInfo.ArgumentList.Add(Path.GetFullPath(childProcessIdFile));
        using var host = Process.Start(startInfo)
                         ?? throw new InvalidOperationException(
                             "Owned Process Tree Host did not start.");
        await WriteProcessIdAsync(hostProcessIdFile, host.Id);
        while (true)
        {
            var line = await host.StandardError.ReadLineAsync();
            if (line is null)
            {
                throw new InvalidDataException(
                    "Owned Process Tree Host exited before reporting its child.");
            }

            const string prefix = "OPENLINEOPS_PROCESS_TREE_ROOT ";
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line[prefix.Length..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2
                && int.TryParse(
                    fields[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId)
                && processId > 0
                && long.TryParse(
                    fields[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var startedAtUnixMilliseconds)
                && startedAtUnixMilliseconds > 0)
            {
                await WriteProcessIdAsync(
                    hostedRootProcessIdFile,
                    processId,
                    startedAtUnixMilliseconds);
                break;
            }
        }

        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> SpawnChildAndExitAsync(
        string childProcessIdFile,
        string releaseFile)
    {
        var lifetime = WindowsCurrentProcessTreeLifetime.BindCurrentProcess();
        using var child = StartLongRunningChild();
        await WriteProcessIdAsync(childProcessIdFile, child.Id);
        var releasePath = Path.GetFullPath(releaseFile);
        while (!File.Exists(releasePath))
        {
            await Task.Delay(10);
        }

        GC.KeepAlive(lifetime);
        return GracefulExitCode;
    }

    private static async Task<int> SpawnChildAndWaitAsync(string childProcessIdFile)
    {
        var lifetime = WindowsCurrentProcessTreeLifetime.BindCurrentProcess();
        using var child = StartLongRunningChild();
        await WriteProcessIdAsync(childProcessIdFile, child.Id);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        GC.KeepAlive(lifetime);
        return 0;
    }

    private static Process StartLongRunningChild()
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
               ?? throw new InvalidOperationException("Long-running child process did not start.");
    }

    private static async Task WriteProcessIdAsync(string path, int processId)
    {
        var fullPath = Path.GetFullPath(path);
        await File.WriteAllTextAsync(
            fullPath,
            processId.ToString(CultureInfo.InvariantCulture));
    }

    private static Task WriteProcessIdAsync(
        string path,
        int processId,
        long startedAtUnixMilliseconds) =>
        File.WriteAllTextAsync(
            Path.GetFullPath(path),
            $"{processId.ToString(CultureInfo.InvariantCulture)} "
            + startedAtUnixMilliseconds.ToString(CultureInfo.InvariantCulture));
}
