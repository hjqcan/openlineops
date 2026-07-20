using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentExecutableContractTests
{
    [Fact]
    public async Task UndeployedReleaseTemplateFailsClosedWithoutAnUnhandledCrashProcess()
    {
        var result = await RunAgentAsync();

        Assert.Equal(70, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "OpenLineOps:WindowsServiceName must contain 1-80 ASCII",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministrativeContentCacheModesAreExposedByAgentExecutable()
    {
        var duplicateProvision = await RunAgentAsync(
            StationAgentCommandLine.ProvisionContentCacheSwitch,
            StationAgentCommandLine.ProvisionContentCacheSwitch);
        Assert.Equal(70, duplicateProvision.ExitCode);
        Assert.Empty(duplicateProvision.StandardOutput);
        Assert.Contains(
            "--provision-content-cache may be specified only once",
            duplicateProvision.StandardError,
            StringComparison.Ordinal);

        var invalidRemoval = await RunAgentAsync(
            StationAgentCommandLine.RemoveContentCachePackageSwitch,
            new string('A', 64));
        Assert.Equal(70, invalidRemoval.ExitCode);
        Assert.Empty(invalidRemoval.StandardOutput);
        Assert.Contains(
            "--remove-content-cache-package requires one lowercase SHA-256 value",
            invalidRemoval.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisioningModeClassifiesCallerWithoutGenericTokenAccessFailure()
    {
        var result = await RunAgentAsync(
            StationAgentCommandLine.ProvisionContentCacheSwitch);

        Assert.Equal(70, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            IsCurrentAdministrativeWindowsCaller()
                ? "OpenLineOps:WindowsServiceName must contain 1-80 ASCII"
                : "requires an elevated Windows administrator or LocalSystem process",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Access is denied",
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AgentProcessResult> RunAgentAsync(params string[] arguments)
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var name in startInfo.Environment.Keys.Where(name =>
                     name.StartsWith("OpenLineOps__Agent__", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(
                         name,
                         "OpenLineOps__WindowsServiceName",
                         StringComparison.OrdinalIgnoreCase)
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
            throw new TimeoutException("Station Agent contract probe did not fail closed within ten seconds.");
        }

        return new AgentProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
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

    private static bool IsCurrentAdministrativeWindowsCaller()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true
               || new WindowsPrincipal(identity).IsInRole(
                   WindowsBuiltInRole.Administrator);
    }

    private sealed record AgentProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
