using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenLineOps.ContentProtection;

namespace OpenLineOps.ContentProtection.Tests;

public sealed class ImmutableContentProtectorTests
{
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

            Assert.ThrowsAny<Exception>(() => File.WriteAllText(fixture.DependencyPath, "changed"));
            Assert.ThrowsAny<Exception>(() => File.Move(
                fixture.DependencyPath,
                fixture.DependencyPath + ".moved"));
            Assert.ThrowsAny<Exception>(() => File.Delete(fixture.DependencyPath));
            Assert.True(File.Exists(fixture.DependencyPath));
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
        var readerSid = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent(TokenAccessLevels.Query).User?.Value
              ?? throw new InvalidOperationException("Current Windows identity has no SID.")
            : "unix-reader";
        return new ContentFixture(
            cacheRoot,
            contentDirectory,
            dependencyPath,
            dependencyBytes,
            inventory,
            new ImmutableContentProtectionPolicy(readerSid),
            new ImmutableContentProtector());
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
