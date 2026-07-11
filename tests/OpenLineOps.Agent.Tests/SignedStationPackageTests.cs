using System.IO.Compression;
using System.Security.Cryptography;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Infrastructure.Packages;
using OpenLineOps.ContentProtection;
using OpenLineOps.Projects.Infrastructure.Releases;

namespace OpenLineOps.Agent.Tests;

public sealed class SignedStationPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-package-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InstallerVerifiesSignatureEveryHashAndCreatesReadOnlyCache()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        var packagePath = Path.Combine(_root, "out", "line.olopkg");
        var built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
            source,
            packagePath,
            "package-line-a",
            "project-a",
            "application-line",
            "snapshot-001",
            "line.main",
            "station.eol",
            "factory-signing",
            rsa.ExportRSAPrivateKeyPem(),
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));
        var installer = CreateInstaller(rsa.ExportSubjectPublicKeyInfoPem());

        var installed = await installer.InstallAsync(
            packagePath,
            built.Manifest.ContentSha256);
        var installedAgain = await installer.InstallAsync(
            packagePath,
            built.Manifest.ContentSha256);

        Assert.Equal(installed.ContentDirectory, installedAgain.ContentDirectory);
        Assert.Equal("application-line", installed.Manifest.ApplicationId);
        Assert.True(File.Exists(Path.Combine(installed.ContentDirectory, "flows", "main.json")));
        var deploymentProvider = new SignedStationMaterialArrivalDeploymentProvider(
            new SignedStationMaterialArrivalDeploymentOptions(
                "agent.line-a",
                "station.line-a",
                packagePath,
                built.Manifest.ContentSha256),
            installer);
        var deployment = await deploymentProvider.GetCurrentAsync();
        Assert.Equal("project-a", deployment.ProjectId);
        Assert.Equal("application-line", deployment.ApplicationId);
        Assert.Equal("snapshot-001", deployment.ProjectSnapshotId);
        Assert.Equal("line.main", deployment.ProductionLineDefinitionId);
        Assert.Equal("station.eol", deployment.StationSystemId);
        Assert.Equal(built.Manifest.ContentSha256, deployment.PackageContentSha256);
        Assert.All(
            Directory.EnumerateFiles(installed.ContentDirectory, "*", SearchOption.AllDirectories),
            path => Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)));
    }

    [Fact]
    public async Task InstallerRejectsContentTamperingBeforeItReachesRuntime()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        var packagePath = Path.Combine(_root, "out", "tampered.olopkg");
        var built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
            source,
            packagePath,
            "package-line-a",
            "project-a",
            "application-line",
            "snapshot-001",
            "line.main",
            "station.eol",
            "factory-signing",
            rsa.ExportRSAPrivateKeyPem(),
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("flows/main.json")!;
            entry.Delete();
            var replacement = archive.CreateEntry("flows/main.json");
            await using var stream = replacement.Open();
            await stream.WriteAsync("{\"tampered\":true}"u8.ToArray());
        }

        var installer = CreateInstaller(rsa.ExportSubjectPublicKeyInfoPem());
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(packagePath, built.Manifest.ContentSha256));

        Assert.Contains("length", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallerRejectsUntrustedSigningKey()
    {
        var source = CreateSource();
        using var signer = RSA.Create(3072);
        using var other = RSA.Create(3072);
        var packagePath = Path.Combine(_root, "out", "untrusted.olopkg");
        var built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
            source,
            packagePath,
            "package-line-a",
            "project-a",
            "application-line",
            "snapshot-001",
            "line.main",
            "station.eol",
            "factory-signing",
            signer.ExportRSAPrivateKeyPem(),
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));
        var installer = CreateInstaller(other.ExportSubjectPublicKeyInfoPem());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(packagePath, built.Manifest.ContentSha256));

        Assert.Contains("verification failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuilderRejectsRsaPrivateKeyBelowSecurityBaseline()
    {
        var source = CreateSource();
        using var weak = RSA.Create(2048);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
                source,
                Path.Combine(_root, "out", "weak.olopkg"),
                "package-line-a",
                "project-a",
                "application-line",
                "snapshot-001",
                "line.main",
                "station.eol",
                "factory-signing",
                weak.ExportRSAPrivateKeyPem(),
                new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero))));

        Assert.Contains("3072", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerRejectsTrustedRsaPublicKeyBelowSecurityBaseline()
    {
        var source = CreateSource();
        using var signer = RSA.Create(3072);
        using var weakTrust = RSA.Create(2048);
        var packagePath = Path.Combine(_root, "out", "strong.olopkg");
        _ = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
            source,
            packagePath,
            "package-line-a",
            "project-a",
            "application-line",
            "snapshot-001",
            "line.main",
            "station.eol",
            "factory-signing",
            signer.ExportRSAPrivateKeyPem(),
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));
        var exception = Assert.Throws<InvalidDataException>(() =>
            CreateInstaller(weakTrust.ExportSubjectPublicKeyInfoPem()));

        Assert.Contains("3072", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuilderRejectsPrivateKeyMaterialInFrozenInventory()
    {
        var source = CreateSource();
        using var signer = RSA.Create(3072);
        await File.WriteAllTextAsync(
            Path.Combine(source, "vendor", "private-key.pem"),
            signer.ExportRSAPrivateKeyPem());
        var packagePath = Path.Combine(_root, "out", "leaked-key.olopkg");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
                source,
                packagePath,
                "package-line-a",
                "project-a",
                "application-line",
                "snapshot-001",
                "line.main",
                "station.eol",
                "factory-signing",
                signer.ExportRSAPrivateKeyPem(),
                new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero))));

        Assert.Contains("private key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(packagePath));
    }

    [Fact]
    public async Task BuilderAllowsPemPublicTrustMaterialInFrozenInventory()
    {
        var source = CreateSource();
        using var signer = RSA.Create(3072);
        await File.WriteAllTextAsync(
            Path.Combine(source, "vendor", "public-cert.pem"),
            signer.ExportSubjectPublicKeyInfoPem());
        var packagePath = Path.Combine(_root, "out", "public-cert.olopkg");

        var built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
            source,
            packagePath,
            "package-line-a",
            "project-a",
            "application-line",
            "snapshot-001",
            "line.main",
            "station.eol",
            "factory-signing",
            signer.ExportRSAPrivateKeyPem(),
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));

        Assert.True(File.Exists(built.PackagePath));
        Assert.Contains(built.Manifest.Entries, entry => entry.Path == "vendor/public-cert.pem");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        DeleteProtectedCacheEntries(Path.Combine(_root, "cache"));
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     _root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(_root, File.GetAttributes(_root) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, recursive: true);
    }

    private static void DeleteProtectedCacheEntries(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        var protector = new ImmutableContentProtector();
        foreach (var contentDirectory in Directory.EnumerateDirectories(cacheRoot).ToArray())
        {
            var leaf = Path.GetFileName(contentDirectory);
            if (leaf.Length == 64
                && leaf.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                protector.DeleteProtectedInstallation(cacheRoot, contentDirectory);
            }
        }
    }

    private string CreateSource()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "flows"));
        Directory.CreateDirectory(Path.Combine(source, "vendor"));
        File.WriteAllText(Path.Combine(source, "application.oloapp"), "{\"applicationId\":\"application-line\"}");
        File.WriteAllText(Path.Combine(source, "flows", "main.json"), "{\"nodes\":[]}");
        File.WriteAllBytes(Path.Combine(source, "vendor", "helper.exe"), [1, 2, 3, 4]);
        return source;
    }

    private SignedStationPackageInstaller CreateInstaller(string publicKeyPem) => new(
        new StationPackageTrustOptions(
            Path.Combine(_root, "cache"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["factory-signing"] = publicKeyPem
            }));
}
