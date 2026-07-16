using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.ContentProtection.Tests;

public sealed class WindowsContentAccessAuthorizerTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void ReadExecuteAuthorizationUsesExplicitRulesAcrossTheTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot();
        var childDirectory = Path.Combine(root, "runtime");
        var childFile = Path.Combine(childDirectory, "dependency.bin");
        Directory.CreateDirectory(childDirectory);
        File.WriteAllText(childFile, "content");
        var readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        try
        {
            WindowsContentAccessAuthorizer.GrantReadExecute(root, readerSid);

            AssertDirectoryRule(
                root,
                readerSid,
                FileSystemRights.ReadAndExecute);
            AssertDirectoryRule(
                childDirectory,
                readerSid,
                FileSystemRights.ReadAndExecute);
            AssertFileRule(
                childFile,
                readerSid,
                FileSystemRights.ReadAndExecute);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WorkspaceAuthorizationGrantsModifyThroughStableHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot();
        var childFile = Path.Combine(root, "result.json");
        File.WriteAllText(childFile, "{}");
        var readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        try
        {
            WindowsContentAccessAuthorizer.GrantWorkspaceModify(root, readerSid);

            AssertDirectoryRule(root, readerSid, FileSystemRights.Modify);
            AssertFileRule(childFile, readerSid, FileSystemRights.Modify);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DescendantReparsePointIsRejectedWithoutAuthorizingItsTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot();
        var target = CreateRoot();
        var link = Path.Combine(root, "outside");
        CreateJunction(link, target);
        var targetSecurity = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(target));
        var targetDacl = targetSecurity.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access);
        var readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                WindowsContentAccessAuthorizer.GrantReadExecute(root, readerSid));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            var unchangedSecurity = FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(target));
            Assert.Equal(
                targetDacl,
                unchangedSecurity.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AuthorizationRejectsBlockingCapabilityDeny()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot();
        var childFile = Path.Combine(root, "blocked.bin");
        File.WriteAllText(childFile, "content");
        var readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        var identity = new SecurityIdentifier(readerSid);
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(root));
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), security);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                WindowsContentAccessAuthorizer.GrantReadExecute(root, readerSid));

            Assert.Contains("effective access", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AuthorizationRejectsGenericAllCapabilityDeny()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int genericAll = 0x10000000;
        var root = CreateRoot();
        var childFile = Path.Combine(root, "generic-deny.bin");
        File.WriteAllText(childFile, "content");
        var readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        var identity = new SecurityIdentifier(readerSid);
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(root));
        var descriptor = new CommonSecurityDescriptor(
            isContainer: true,
            isDS: false,
            security.GetSecurityDescriptorBinaryForm(),
            offset: 0);
        var discretionaryAcl = descriptor.DiscretionaryAcl
                               ?? throw new InvalidDataException(
                                   "The test directory has no discretionary ACL.");
        discretionaryAcl.AddAccess(
            AccessControlType.Deny,
            identity,
            genericAll,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None);
        var updatedDescriptor = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(updatedDescriptor, 0);
        security.SetSecurityDescriptorBinaryForm(
            updatedDescriptor,
            AccessControlSections.Access);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), security);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                WindowsContentAccessAuthorizer.GrantReadExecute(root, readerSid));

            Assert.Contains("effective access", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AuthorizationRejectsNullDaclInsteadOfMaterializingEveryoneFullControl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot();
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(root));
        security.SetSecurityDescriptorSddlForm(
            "D:NO_ACCESS_CONTROL",
            AccessControlSections.Access);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), security);
        var readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                WindowsContentAccessAuthorizer.GrantReadExecute(root, readerSid));

            Assert.Contains("NULL DACL", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void RuntimeVerificationRejectsCapabilityWriteAccessOnAnyDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot();
        var childDirectory = Path.Combine(root, "Lib");
        var childFile = Path.Combine(childDirectory, "module.py");
        Directory.CreateDirectory(childDirectory);
        File.WriteAllText(childFile, "content");
        var readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.PythonRuntimeCapabilityName);
        var identity = new SecurityIdentifier(readerSid);
        try
        {
            WindowsContentAccessAuthorizer.GrantReadExecute(root, readerSid);
            var childSecurity = FileSystemAclExtensions.GetAccessControl(new FileInfo(childFile));
            childSecurity.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(childFile), childSecurity);

            var exception = Assert.Throws<InvalidDataException>(() =>
                WindowsContentAccessAuthorizer.VerifyReadExecute(root, readerSid));

            Assert.Contains("least privilege", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void HardLinkCannotRedirectAuthorizationOutsideTheRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot();
        var targetRoot = CreateRoot();
        var targetFile = Path.Combine(targetRoot, "outside.bin");
        var linkedFile = Path.Combine(root, "redirected.bin");
        File.WriteAllText(targetFile, "outside");
        Assert.True(
            CreateHardLink(linkedFile, targetFile, IntPtr.Zero),
            new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var targetAcl = FileSystemAclExtensions
            .GetAccessControl(new FileInfo(targetFile))
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
            WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                WindowsContentAccessAuthorizer.GrantReadExecute(root, readerSid));

            Assert.Contains("multiply linked", exception.Message, StringComparison.Ordinal);
            var unchangedAcl = FileSystemAclExtensions
                .GetAccessControl(new FileInfo(targetFile))
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.Equal(targetAcl, unchangedAcl);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(targetRoot, recursive: true);
        }
    }

    [Fact]
    public void CanonicalizationPreservesTheVolumeRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var volumeRoot = Path.GetPathRoot(Path.GetTempPath())
                         ?? throw new InvalidOperationException(
                             "The Windows temporary directory has no volume root.");
        var normalize = typeof(WindowsContentAccessAuthorizer).GetMethod(
            "NormalizeRootDirectory",
            BindingFlags.NonPublic | BindingFlags.Static)
                        ?? throw new InvalidOperationException(
                            "Content authorization root canonicalization is missing.");

        Assert.Equal(volumeRoot, normalize.Invoke(null, [volumeRoot]));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-content-authorization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                link,
                target
            }
        }) ?? throw new InvalidOperationException("Could not start the junction fixture command.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("The junction fixture command timed out.");
        }
        Assert.Equal(0, process.ExitCode);
        Assert.True(
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
            "The junction fixture is not a reparse point.");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertDirectoryRule(
        string path,
        string readerSid,
        FileSystemRights expectedRights)
    {
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path));
        var rule = Assert.Single(FindExplicitAllowRules(security, readerSid));
        Assert.Equal(expectedRights, rule.FileSystemRights & expectedRights);
        Assert.True((rule.FileSystemRights & FileSystemRights.Synchronize) != 0);
        Assert.Equal(
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            rule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertFileRule(
        string path,
        string readerSid,
        FileSystemRights expectedRights)
    {
        var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        var rule = Assert.Single(FindExplicitAllowRules(security, readerSid));
        Assert.Equal(expectedRights, rule.FileSystemRights & expectedRights);
        Assert.True((rule.FileSystemRights & FileSystemRights.Synchronize) != 0);
        Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule[] FindExplicitAllowRules(
        FileSystemSecurity security,
        string readerSid)
    {
        var identity = new SecurityIdentifier(readerSid);
        return security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow
                           && identity.Equals(rule.IdentityReference))
            .ToArray();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
