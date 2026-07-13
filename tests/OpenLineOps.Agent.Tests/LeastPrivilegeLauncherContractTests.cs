using System.Diagnostics;

namespace OpenLineOps.Agent.Tests;

public sealed class LeastPrivilegeLauncherContractTests
{
    private const string Identity = "RestrictedCurrentLowIntegrity";

    public static TheoryData<string[], string> RejectedCommands => new()
    {
        {
            [],
            "Expected exactly the non-interactive RestrictedCurrentLowIntegrity worker launch protocol."
        },
        {
            ["-n", "-u", "another-identity", "--", "env"],
            "Expected exactly the non-interactive RestrictedCurrentLowIntegrity worker launch protocol."
        },
        {
            [
                "-n",
                "-u",
                Identity,
                "--",
                "env",
                "UNREVIEWED=value",
                RequiredIsolationAssignment(),
                WorkerPath()
            ],
            "Unsupported least-privilege worker environment assignment 'UNREVIEWED'."
        },
        {
            [
                "-n",
                "-u",
                Identity,
                "--",
                "env",
                RequiredIsolationAssignment(),
                RequiredLeastPrivilegeAssignment(),
                RequiredIdentityAssignment(),
                Path.Combine(Environment.SystemDirectory, "whoami.exe")
            ],
            "The launcher can execute only the co-packaged OpenLineOps.ScriptWorker.exe."
        },
        {
            [
                "-n",
                "-u",
                Identity,
                "--",
                "env",
                RequiredIsolationAssignment(),
                RequiredLeastPrivilegeAssignment(),
                RequiredIdentityAssignment(),
                WorkerPath(),
                "unexpected-worker-argument"
            ],
            "The bundled Python Script Worker does not accept launcher arguments."
        }
    };

    [Theory]
    [MemberData(nameof(RejectedCommands))]
    public async Task LauncherRejectsEveryProtocolExtension(
        string[] arguments,
        string expectedError)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executablePath = Path.Combine(
            AppContext.BaseDirectory,
            "OpenLineOps.LeastPrivilegeLauncher.exe");
        Assert.True(File.Exists(executablePath), $"Missing test launcher: {executablePath}");

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Least Privilege Launcher did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(78, process.ExitCode);
        Assert.Equal(string.Empty, await standardOutput);
        Assert.Contains(
            expectedError,
            await standardError,
            StringComparison.Ordinal);
    }

    private static string WorkerPath() => Path.Combine(
        AppContext.BaseDirectory,
        "OpenLineOps.ScriptWorker.exe");

    private static string RequiredIsolationAssignment() =>
        "OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE=LeastPrivilegeIdentity";

    private static string RequiredLeastPrivilegeAssignment() =>
        "OPENLINEOPS_SCRIPT_WORKER_SANDBOX_REQUIRE_LEAST_PRIVILEGE=True";

    private static string RequiredIdentityAssignment() =>
        $"OPENLINEOPS_SCRIPT_WORKER_SANDBOX_IDENTITY={Identity}";
}
