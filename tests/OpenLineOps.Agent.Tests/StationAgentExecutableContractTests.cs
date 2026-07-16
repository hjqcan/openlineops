using System.Diagnostics;
using System.Text;

namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentExecutableContractTests
{
    [Fact]
    public async Task UndeployedReleaseTemplateFailsClosedWithoutAnUnhandledCrashProcess()
    {
        var executableDirectory = AgentExecutableDirectory();
        var executablePath = Path.Combine(
            executableDirectory,
            OperatingSystem.IsWindows() ? "OpenLineOps.Agent.exe" : "OpenLineOps.Agent");
        Assert.True(File.Exists(executablePath), $"Station Agent executable is missing: {executablePath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = executableDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var name in startInfo.Environment.Keys.Where(name =>
                     name.StartsWith("OpenLineOps__Agent__", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(name, "DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(name, "ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Station Agent process could not be started.");
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Undeployed Station Agent did not fail closed within ten seconds.");
        }

        Assert.Equal(70, process.ExitCode);
        Assert.Empty(await standardOutput);
        Assert.Contains(
            "AgentId must satisfy the shared Station identity contract",
            await standardError,
            StringComparison.Ordinal);
    }

    private static string AgentExecutableDirectory()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        var repositoryRoot = directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "OpenLineOps repository root could not be found.");
        return Path.Combine(
            repositoryRoot,
            "src",
            "OpenLineOps.Agent",
            "bin",
            configuration,
            "net10.0");
    }
}
