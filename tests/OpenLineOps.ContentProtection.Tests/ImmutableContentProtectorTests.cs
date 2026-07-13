using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.ContentProtection.Tests;

public sealed class ImmutableContentProtectorTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsPolicyRequiresDistinctCapabilityAndCurrentHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var capabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
            var protector = new ImmutableContentProtector();

            var missingHost = Assert.Throws<InvalidDataException>(() =>
                protector.ProtectCacheBoundary(
                    cacheRoot,
                    new ImmutableContentProtectionPolicy(capabilitySid)));
            Assert.Contains("HostReaderSid is required", missingHost.Message, StringComparison.Ordinal);

            var sameIdentity = Assert.Throws<InvalidDataException>(() =>
                protector.ProtectCacheBoundary(
                    cacheRoot,
                    new ImmutableContentProtectionPolicy(capabilitySid, capabilitySid)));
            Assert.Contains("different identities", sameIdentity.Message, StringComparison.Ordinal);

            var wrongHost = Assert.Throws<InvalidDataException>(() =>
                protector.ProtectCacheBoundary(
                    cacheRoot,
                    new ImmutableContentProtectionPolicy(
                        capabilitySid,
                        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value)));
            Assert.Contains("current trusted", wrongHost.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cacheRoot);
        }
    }

    [Fact]
    public async Task ProtectedCacheRejectsWriteRenameAndDelete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        try
        {
            await fixture.Protector.ProtectAsync(
                fixture.ContentDirectory,
                fixture.Inventory,
                fixture.Policy);
            await fixture.Protector.VerifyAsync(
                fixture.ContentDirectory,
                fixture.Inventory,
                fixture.Policy);
            fixture.Protector.VerifyCacheBoundary(fixture.CacheRoot, fixture.Policy);
            AssertOwnedByHost(fixture.CacheRoot, fixture.Policy);
            AssertOwnedByHost(fixture.ContentDirectory, fixture.Policy);
            AssertOwnedByHost(fixture.DependencyPath, fixture.Policy);

            Assert.ThrowsAny<Exception>(() => File.WriteAllText(fixture.DependencyPath, "changed"));
            Assert.ThrowsAny<Exception>(() => File.Move(
                fixture.DependencyPath,
                fixture.DependencyPath + ".moved"));
            Assert.ThrowsAny<Exception>(() => File.Delete(fixture.DependencyPath));
            Assert.True(File.Exists(fixture.DependencyPath));
            Assert.ThrowsAny<Exception>(() => Directory.Move(
                fixture.ContentDirectory,
                fixture.ContentDirectory + ".moved"));
            Assert.ThrowsAny<Exception>(() => Directory.Delete(
                fixture.ContentDirectory,
                recursive: true));
            Assert.True(Directory.Exists(fixture.ContentDirectory));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Fact]
    public async Task ProtectedCachePromotesStagingAndProtectsMultipleContentRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        var secondContentDirectory = Path.Combine(fixture.CacheRoot, new string('b', 64));
        var stagingDirectory = Path.Combine(
            fixture.CacheRoot,
            $".staging.{Guid.NewGuid():N}");
        try
        {
            await fixture.Protector.ProtectAsync(
                fixture.ContentDirectory,
                fixture.Inventory,
                fixture.Policy);

            Directory.CreateDirectory(stagingDirectory);
            var secondDependencyPath = Path.Combine(stagingDirectory, "runtime", "dependency.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(secondDependencyPath)!);
            await File.WriteAllBytesAsync(secondDependencyPath, fixture.DependencyBytes);
            Directory.Move(stagingDirectory, secondContentDirectory);

            await fixture.Protector.ProtectAsync(
                secondContentDirectory,
                fixture.Inventory,
                fixture.Policy);
            await fixture.Protector.VerifyAsync(
                fixture.ContentDirectory,
                fixture.Inventory,
                fixture.Policy);
            await fixture.Protector.VerifyAsync(
                secondContentDirectory,
                fixture.Inventory,
                fixture.Policy);

            Assert.ThrowsAny<Exception>(() => Directory.Move(
                secondContentDirectory,
                secondContentDirectory + ".moved"));
            Assert.ThrowsAny<Exception>(() => Directory.Delete(
                secondContentDirectory,
                recursive: true));
        }
        finally
        {
            if (Directory.Exists(secondContentDirectory))
            {
                fixture.Protector.DeleteProtectedInstallation(
                    fixture.CacheRoot,
                    secondContentDirectory);
            }

            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            fixture.Delete();
        }
    }

    [Fact]
    public async Task VerificationRejectsMissingReaderMutationDenyRule()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        try
        {
            await fixture.Protector.ProtectAsync(
                fixture.ContentDirectory,
                fixture.Inventory,
                fixture.Policy);

            var reader = new SecurityIdentifier(fixture.Policy.ReaderSid);
            var fileInfo = new FileInfo(fixture.DependencyPath);
            var security = FileSystemAclExtensions.GetAccessControl(fileInfo);
            security.PurgeAccessRules(reader);
            security.AddAccessRule(new FileSystemAccessRule(
                reader,
                FileSystemRights.ReadAndExecute,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(fileInfo, security);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Protector.VerifyAsync(
                    fixture.ContentDirectory,
                    fixture.Inventory,
                    fixture.Policy));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task VerificationRejectsReaderMutationDenyWithInheritOnlyPropagation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        try
        {
            await fixture.Protector.ProtectAsync(
                fixture.ContentDirectory,
                fixture.Inventory,
                fixture.Policy);

            var reader = new SecurityIdentifier(fixture.Policy.ReaderSid);
            var contentInfo = new DirectoryInfo(fixture.ContentDirectory);
            var security = FileSystemAclExtensions.GetAccessControl(contentInfo);
            var originalRules = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            var mutationDeny = originalRules.Single(rule =>
                reader.Equals(rule.IdentityReference)
                && rule.AccessControlType == AccessControlType.Deny);
            security.RemoveAccessRuleSpecific(mutationDeny);
            security.AddAccessRule(new FileSystemAccessRule(
                reader,
                mutationDeny.FileSystemRights,
                mutationDeny.InheritanceFlags,
                PropagationFlags.InheritOnly,
                AccessControlType.Deny));
            FileSystemAclExtensions.SetAccessControl(contentInfo, security);

            var tamperedRules = FileSystemAclExtensions
                .GetAccessControl(contentInfo)
                .GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            Assert.Equal(originalRules.Length, tamperedRules.Length);
            Assert.Contains(tamperedRules, rule =>
                reader.Equals(rule.IdentityReference)
                && rule.AccessControlType == AccessControlType.Deny
                && rule.FileSystemRights == mutationDeny.FileSystemRights
                && rule.InheritanceFlags == mutationDeny.InheritanceFlags
                && rule.PropagationFlags == PropagationFlags.InheritOnly);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Protector.VerifyAsync(
                    fixture.ContentDirectory,
                    fixture.Inventory,
                    fixture.Policy));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task VerificationRejectsMissingCacheBoundaryDeleteChildDenyRule()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        try
        {
            await fixture.Protector.ProtectAsync(
                fixture.ContentDirectory,
                fixture.Inventory,
                fixture.Policy);

            var reader = new SecurityIdentifier(fixture.Policy.ReaderSid);
            var cacheInfo = new DirectoryInfo(fixture.CacheRoot);
            var security = FileSystemAclExtensions.GetAccessControl(cacheInfo);
            var mutationDeny = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Single(rule =>
                    reader.Equals(rule.IdentityReference)
                    && rule.AccessControlType == AccessControlType.Deny);
            security.RemoveAccessRuleSpecific(mutationDeny);
            FileSystemAclExtensions.SetAccessControl(cacheInfo, security);

            Assert.Throws<InvalidDataException>(() =>
                fixture.Protector.VerifyCacheBoundary(fixture.CacheRoot, fixture.Policy));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Fact]
    public async Task VerificationRejectsDependencyTamperingAndExtraContent()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.DependencyPath, "tampered", Encoding.UTF8);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Protector.VerifyAsync(
                    fixture.ContentDirectory,
                    fixture.Inventory,
                    fixture.Policy));

            await File.WriteAllBytesAsync(fixture.DependencyPath, fixture.DependencyBytes);
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ContentDirectory, "unexpected.txt"),
                "unexpected");
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Protector.VerifyAsync(
                    fixture.ContentDirectory,
                    fixture.Inventory,
                    fixture.Policy));
        }
        finally
        {
            fixture.Delete();
        }
    }

    private static async Task<ContentFixture> CreateFixtureAsync()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"openlineops-cache-{Guid.NewGuid():N}");
        var contentDirectory = Path.Combine(cacheRoot, new string('a', 64));
        var dependencyPath = Path.Combine(contentDirectory, "runtime", "dependency.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dependencyPath)!);
        var dependencyBytes = Encoding.UTF8.GetBytes("immutable dependency");
        await File.WriteAllBytesAsync(dependencyPath, dependencyBytes);
        var inventory = new[]
        {
            new ImmutableContentFile(
                "runtime/dependency.bin",
                dependencyBytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(dependencyBytes)))
        };
        var readerSid = "unix-reader";
        string? hostReaderSid = null;
        if (OperatingSystem.IsWindows())
        {
            readerSid = WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            hostReaderSid = identity.User?.Value
                            ?? throw new InvalidOperationException(
                                "Current Windows identity has no SID.");
        }

        return new ContentFixture(
            cacheRoot,
            contentDirectory,
            dependencyPath,
            dependencyBytes,
            inventory,
            new ImmutableContentProtectionPolicy(readerSid, hostReaderSid),
            new ImmutableContentProtector());
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnedByHost(
        string path,
        ImmutableContentProtectionPolicy policy)
    {
        var security = Directory.Exists(path)
            ? (FileSystemSecurity)FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(path))
            : FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        Assert.Equal(
            new SecurityIdentifier(policy.HostReaderSid!),
            security.GetOwner(typeof(SecurityIdentifier)));
    }

    private sealed record ContentFixture(
        string CacheRoot,
        string ContentDirectory,
        string DependencyPath,
        byte[] DependencyBytes,
        IReadOnlyCollection<ImmutableContentFile> Inventory,
        ImmutableContentProtectionPolicy Policy,
        ImmutableContentProtector Protector)
    {
        public void Delete()
        {
            if (Directory.Exists(ContentDirectory))
            {
                Protector.DeleteProtectedInstallation(CacheRoot, ContentDirectory);
            }

            if (Directory.Exists(CacheRoot))
            {
                Directory.Delete(CacheRoot);
            }
        }
    }
}
