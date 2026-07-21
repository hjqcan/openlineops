using System.Diagnostics;

namespace OpenLineOps.Agent.Tests;

public sealed class WindowsServiceTokenTestHelperContractTests
{
    [Fact]
    public async Task SelfContainedHelperRejectsInvocationOutsideFixedProtocol()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var helperRoot = Path.Combine(
            AppContext.BaseDirectory,
            "windows-service-token-test-helper");
        var helperPath = Path.Combine(
            helperRoot,
            "OpenLineOps.WindowsServiceToken.TestHelper.exe");
        Assert.True(File.Exists(helperPath), $"Missing staged helper: {helperPath}");
        Assert.True(
            File.Exists(Path.Combine(helperRoot, "coreclr.dll")),
            "The SCM test helper must be self-contained and must not depend on the runner user's .NET installation.");

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.Environment.Remove("DOTNET_ROOT");
        startInfo.Environment.Remove("DOTNET_HOST_PATH");

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "Could not start the self-contained service-token test helper.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(64, process.ExitCode);
            Assert.Contains("accepts exactly", await standardError, StringComparison.Ordinal);
            Assert.Empty(await standardOutput);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
