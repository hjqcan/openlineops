using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Execution;

namespace OpenLineOps.Agent.SafetyActuator.TestHelper;

public sealed class SafetyActuatorTestHelperMarker;

internal static class Program
{
    private const int UsageExitCode = 64;
    private const int FailureExitCode = 23;

    public static async Task<int> Main(string[] args)
    {
        if (args is ["actuator-owner", var workingDirectory])
        {
            return await RunActuatorOwnerAsync(workingDirectory);
        }

        if (args is ["safety-child", var processIdFile, var delayText]
            && int.TryParse(
                delayText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var childDelayMilliseconds)
            && childDelayMilliseconds > 0)
        {
            await WriteProcessIdAsync(processIdFile);
            await Task.Delay(childDelayMilliseconds);
            return 0;
        }

        if (args.Length == 0
            || (args[0] is not "emergency-stop" and not "safe-stop"))
        {
            return UsageExitCode;
        }

        var options = ParseOptions(args[1..]);
        if (!options.TryGetValue("reason", out var reason))
        {
            return UsageExitCode;
        }

        return reason switch
        {
            "clean-success" => 0,
            "execution-failure" => FailureExitCode,
            "process-limit" => await ObserveProcessLimitAsync(
                RequiredOption(options, "requested-by")),
            "root-exit-child" => StartChild(
                RequiredOption(options, "requested-by"),
                delayMilliseconds: 750),
            "wait-for-termination" => await StartChildAndWaitAsync(
                RequiredOption(options, "requested-by")),
            "wait-for-owner-termination" =>
                await StartOwnedTreeAndWaitAsync(
                    RequiredOption(options, "requested-by")),
            _ => UsageExitCode
        };
    }

    private static async Task<int> RunActuatorOwnerAsync(
        string workingDirectory)
    {
        var root = Path.GetFullPath(workingDirectory);
        Directory.CreateDirectory(root);
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException(
                             "Safety actuator owner executable is unavailable.");
        var actuator = new ProcessStationSafetyActuator(
            new ProcessStationSafetyOptions(
                executable,
                root,
                TimeSpan.FromMinutes(5)));
        var result = await actuator.EmergencyStopAsync(
            new EmergencyStopRequested(
                Guid.NewGuid(),
                $"owner/{Guid.NewGuid():N}",
                "agent-owner-test",
                "station-owner-test",
                "wait-for-owner-termination",
                root,
                DateTimeOffset.UtcNow));
        return result.Accepted ? 0 : FailureExitCode;
    }

    private static async Task<int> ObserveProcessLimitAsync(
        string observationFile)
    {
        var children = new List<Process>();
        var limitObserved = false;
        try
        {
            for (var index = 0; index < 32; index++)
            {
                try
                {
                    children.Add(StartChildProcess(
                        Path.Combine(
                            Environment.CurrentDirectory,
                            $"resource-child-{index}.pid"),
                        delayMilliseconds: 300_000));
                }
                catch (Exception exception) when (
                    exception is Win32Exception or InvalidOperationException)
                {
                    limitObserved = true;
                    break;
                }
            }

            await File.WriteAllTextAsync(
                Path.GetFullPath(observationFile),
                $"{children.Count.ToString(CultureInfo.InvariantCulture)}"
                + $":{limitObserved}");
            return limitObserved ? 0 : FailureExitCode;
        }
        finally
        {
            foreach (var child in children)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }

            var exits = children.Select(WaitForExitAsync).ToArray();
            await Task.WhenAll(exits)
                .WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var child in children)
            {
                child.Dispose();
            }
        }
    }

    private static async Task WaitForExitAsync(Process child)
    {
        try
        {
            await child.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static int StartChild(
        string processIdFile,
        int delayMilliseconds)
    {
        using var child = StartChildProcess(processIdFile, delayMilliseconds);
        return 0;
    }

    private static async Task<int> StartChildAndWaitAsync(string processIdFile)
    {
        using var child = StartChildProcess(
            processIdFile,
            delayMilliseconds: 300_000);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<int> StartOwnedTreeAndWaitAsync(
        string workingDirectory)
    {
        var root = Path.GetFullPath(workingDirectory);
        Directory.CreateDirectory(root);
        await WriteProcessIdAsync(Path.Combine(root, "safety-root.pid"));
        using var child = StartChildProcess(
            Path.Combine(root, "safety-child.pid"),
            delayMilliseconds: 300_000);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static Process StartChildProcess(
        string processIdFile,
        int delayMilliseconds)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException(
                             "Safety actuator test helper executable is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("safety-child");
        startInfo.ArgumentList.Add(Path.GetFullPath(processIdFile));
        startInfo.ArgumentList.Add(
            delayMilliseconds.ToString(CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException(
                   "Safety actuator test child did not start.");
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0)
        {
            return [];
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)
                || !result.TryAdd(args[index][2..], args[index + 1]))
            {
                return [];
            }
        }

        return result;
    }

    private static string RequiredOption(
        Dictionary<string, string> options,
        string name) =>
        options.TryGetValue(name, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException(
                $"Safety actuator test option '--{name}' is required.");

    private static Task WriteProcessIdAsync(string path) =>
        File.WriteAllTextAsync(
            Path.GetFullPath(path),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
}
