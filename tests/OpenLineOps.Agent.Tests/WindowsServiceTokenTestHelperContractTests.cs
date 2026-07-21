using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceTokenTestHelperContractTests
{
    [Fact]
    public void BridgeTreeOwnerCanonicalizationPreservesAccessRules()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "openlineops-token-owner-" + Guid.NewGuid().ToString("N"));
        var childDirectory = Path.Combine(root, "child");
        var childFile = Path.Combine(childDirectory, "request.json");
        try
        {
            Directory.CreateDirectory(childDirectory);
            var directoryAccessBefore = AccessSddl(new DirectoryInfo(childDirectory));
            WindowsServiceTokenTestBridge.CanonicalizeBridgeTreeOwner(root);
            File.WriteAllText(childFile, "{}");
            var fileAccessBefore = AccessSddl(new FileInfo(childFile));

            WindowsServiceTokenTestBridge.CanonicalizeBridgeTreeOwner(root);

            var expectedOwner = WindowsIdentity.GetCurrent().User
                                ?? throw new InvalidOperationException(
                                    "The test runner has no Windows SID.");
            Assert.Equal(expectedOwner, Owner(new DirectoryInfo(root)));
            Assert.Equal(expectedOwner, Owner(new DirectoryInfo(childDirectory)));
            Assert.Equal(expectedOwner, Owner(new FileInfo(childFile)));
            Assert.Equal(directoryAccessBefore, AccessSddl(new DirectoryInfo(childDirectory)));
            Assert.Equal(fileAccessBefore, AccessSddl(new FileInfo(childFile)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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

    private static SecurityIdentifier? Owner(FileSystemInfo entry)
    {
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? FileSystemAclExtensions.GetAccessControl(
                directory,
                AccessControlSections.Owner)
            : FileSystemAclExtensions.GetAccessControl(
                (FileInfo)entry,
                AccessControlSections.Owner);
        return (SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier));
    }

    private static string AccessSddl(FileSystemInfo entry)
    {
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? FileSystemAclExtensions.GetAccessControl(
                directory,
                AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(
                (FileInfo)entry,
                AccessControlSections.Access);
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }
}
