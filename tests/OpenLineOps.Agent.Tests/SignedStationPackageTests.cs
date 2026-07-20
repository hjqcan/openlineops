using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Infrastructure.Packages;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;
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
        BuiltStationPackage built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
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
        SignedStationPackageInstaller installer = CreateInstaller(rsa.ExportSubjectPublicKeyInfoPem());

        InstalledStationPackage installed = await installer.InstallAsync(
            packagePath,
            built.Manifest.ContentSha256);
        InstalledStationPackage installedAgain = await installer.InstallAsync(
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
        VerifiedStationMaterialArrivalDeployment deployment = await deploymentProvider.GetCurrentAsync();
        Assert.Equal("project-a", deployment.ProjectId);
        Assert.Equal("application-line", deployment.ApplicationId);
        Assert.Equal("snapshot-001", deployment.ProjectSnapshotId);
        Assert.Equal("line.main", deployment.ProductionLineDefinitionId);
        Assert.Equal("station.eol", deployment.StationSystemId);
        Assert.Equal(built.Manifest.ContentSha256, deployment.PackageContentSha256);
        Assert.All(
            Directory.EnumerateFiles(installed.ContentDirectory, "*", SearchOption.AllDirectories),
            path => Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)));
        var commitDirectory = CommitDirectoryFor(installed.ContentDirectory);
        Assert.Equal(
            "content.sha256",
            Path.GetFileName(Assert.Single(Directory.EnumerateFiles(commitDirectory))));
        Assert.Equal(
            built.Manifest.ContentSha256 + "\n",
            await File.ReadAllTextAsync(Path.Combine(commitDirectory, "content.sha256")));
    }

    [Fact]
    public async Task BuilderEmitsCanonicalStoredEntriesAndExtractionVersion()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "stored-contract.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);

        foreach (ClassicCentralRecord record in ReadClassicCentralRecords(package))
        {
            Assert.Equal(
                20,
                BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(record.RecordOffset + 6)));
            Assert.Equal(
                20,
                BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(record.RecordOffset + 4)));
            Assert.Equal(
                0,
                BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(record.RecordOffset + 10)));
            Assert.Equal(
                20,
                BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(record.LocalHeaderOffset + 4)));
            Assert.Equal(
                0,
                BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(record.LocalHeaderOffset + 8)));
            Assert.Equal(
                BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(record.RecordOffset + 20)),
                BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(record.RecordOffset + 24)));
            Assert.Equal(
                0,
                BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(record.RecordOffset + 36)));
            Assert.Equal(
                0u,
                BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(record.RecordOffset + 38)));
        }

        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        InstalledStationPackage installed = await installer.InstallAsync(
            built.PackagePath,
            built.Manifest.ContentSha256);
        Assert.Equal(built.Manifest.ContentSha256, installed.Manifest.ContentSha256);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InstallerPreservesNthProtectionFailureAndResumesSameHash(
        int failureAfterFileCount)
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        var packagePath = Path.Combine(_root, "out", "protection-failure.olopkg");
        BuiltStationPackage built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
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
        var protector = new InventoryOnlyTestContentProtector(
            markFilesReadOnly: true,
            failFirstProtectionAfterFileCount: failureAfterFileCount);
        using var installer = new SignedStationPackageInstaller(
            new StationPackageTrustOptions(
                Path.Combine(_root, "cache"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["factory-signing"] = rsa.ExportSubjectPublicKeyInfoPem()
                },
                ImmutableStationServiceSid:
                    AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()),
            protector);
        var finalDirectory = Path.Combine(_root, "cache", built.Manifest.ContentSha256);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await installer.InstallAsync(packagePath, built.Manifest.ContentSha256));

        Assert.Contains("Injected", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(finalDirectory));
        Assert.False(Directory.Exists(CommitDirectoryFor(finalDirectory)));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(Path.Combine(_root, "cache")),
            path => Path.GetFileName(path).EndsWith(".installing", StringComparison.Ordinal));

        InstalledStationPackage installed = await installer.InstallAsync(packagePath, built.Manifest.ContentSha256);

        Assert.Equal(finalDirectory, installed.ContentDirectory);
        Assert.True(Directory.Exists(finalDirectory));
        Assert.True(Directory.Exists(CommitDirectoryFor(finalDirectory)));
        Assert.All(
            Directory.EnumerateFiles(finalDirectory, "*", SearchOption.AllDirectories),
            path => Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)));
    }

    [Fact]
    public async Task InstallerResumesInterruptedCommitProtection()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        var packagePath = Path.Combine(_root, "out", "interrupted-protection.olopkg");
        BuiltStationPackage built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
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
        var protector = new InventoryOnlyTestContentProtector(
            markFilesReadOnly: true,
            failFirstProtectionAfterFileCount: 1,
            failProtectionCall: 2);
        using var installer = new SignedStationPackageInstaller(
            new StationPackageTrustOptions(
                Path.Combine(_root, "cache"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["factory-signing"] = rsa.ExportSubjectPublicKeyInfoPem()
                },
                ImmutableStationServiceSid:
                    AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()),
            protector);
        var finalDirectory = Path.Combine(_root, "cache", built.Manifest.ContentSha256);

        InvalidOperationException firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await installer.InstallAsync(packagePath, built.Manifest.ContentSha256));

        Assert.Contains("Injected", firstFailure.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(finalDirectory));
        Assert.True(Directory.Exists(CommitDirectoryFor(finalDirectory)));

        InstalledStationPackage installed = await installer.InstallAsync(packagePath, built.Manifest.ContentSha256);

        Assert.Equal(finalDirectory, installed.ContentDirectory);
        Assert.True(File.Exists(Path.Combine(
            CommitDirectoryFor(finalDirectory),
            "content.sha256")));
        Assert.All(
            Directory.EnumerateFiles(finalDirectory, "*", SearchOption.AllDirectories),
            path => Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)));
    }

    [Fact]
    public async Task InstallerRejectsContentTamperingBeforeItReachesRuntime()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        var packagePath = Path.Combine(_root, "out", "tampered.olopkg");
        BuiltStationPackage built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
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
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.GetEntry("flows/main.json")!;
            entry.Delete();
            ZipArchiveEntry replacement = archive.CreateEntry("flows/main.json");
            await using Stream stream = replacement.Open();
            await stream.WriteAsync("{\"tampered\":true}"u8.ToArray());
        }

        SignedStationPackageInstaller installer = CreateInstaller(rsa.ExportSubjectPublicKeyInfoPem());
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(packagePath, built.Manifest.ContentSha256));

        Assert.Contains("canonical stored", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallerRejectsMetadataWhoseActualExpandedLengthExceedsAdvertisedLength()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "expanded-manifest.olopkg", rsa);
        await AppendEntryContentAndForgeAdvertisedLengthAsync(
            built.PackagePath,
            "package.manifest.json",
            additionalExpandedBytes: 1);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("compressed and expanded sizes differ", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "cache")));
    }

    [Fact]
    public async Task InstallerRejectsPayloadWhoseActualExpandedLengthExceedsManifestAndRemovesStaging()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "expanded-payload.olopkg", rsa);
        await AppendEntryContentAndForgeAdvertisedLengthAsync(
            built.PackagePath,
            "flows/main.json",
            additionalExpandedBytes: 1);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("compressed and expanded sizes differ", exception.Message, StringComparison.Ordinal);
        var cache = Path.Combine(_root, "cache");
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }

    [Fact]
    public async Task InstallerRejectsArchiveAboveConfiguredPackageByteLimitWithoutCacheState()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "oversized-archive.olopkg", rsa);
        long packageLength = new FileInfo(built.PackagePath).Length;
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem(),
            maximumPackageBytes: packageLength - 1);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("archive size exceeds", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "cache")));
    }

    [Fact]
    public async Task InstallerRejectsEntryCountBeforeReadingCentralRecords()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "entry-count.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        int footerOffset = FindClassicFooter(package);
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(footerOffset + 8), 20_003);
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(footerOffset + 10), 20_003);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("too many", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "cache")));
    }

    [Fact]
    public async Task InstallerRejectsCentralDirectorySizeBeforeReadingRecords()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "central-size.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        int footerOffset = FindClassicFooter(package);
        uint centralSize = BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(footerOffset + 12));
        var padding = new byte[6 * 1024];
        byte[] forged = InsertBytes(package, footerOffset, padding);
        int forgedFooterOffset = footerOffset + padding.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(
            forged.AsSpan(forgedFooterOffset + 12),
            checked(centralSize + (uint)padding.Length));
        await File.WriteAllBytesAsync(built.PackagePath, forged);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("central directory size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerRejectsOversizedEntryNameBeforeAllocatingIt()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "entry-name.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        int footerOffset = FindClassicFooter(package);
        int centralOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(footerOffset + 16)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(centralOffset + 28),
            StationPackageCanonicalization.MaximumRelativePathUtf8Bytes + 1);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("canonical stored", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalizationRejectsPortablePathAboveUtf8ByteLimit()
    {
        var oversized = new string('a', StationPackageCanonicalization.MaximumRelativePathUtf8Bytes + 1);

        Assert.Throws<InvalidDataException>(() =>
            StationPackageCanonicalization.NormalizeRelativePath(oversized, nameof(oversized)));
    }

    [Theory]
    [MemberData(nameof(InvalidPortablePackagePaths))]
    public void CanonicalizationRejectsNonPortablePaths(string path)
    {
        Assert.Throws<InvalidDataException>(() =>
            StationPackageCanonicalization.NormalizeRelativePath(path, nameof(path)));
    }

    [Fact]
    public void CanonicalizationAcceptsNfcUnicodePortablePath()
    {
        const string path = "vendor/mesure-\u00e9talon.txt";

        Assert.Equal(
            path,
            StationPackageCanonicalization.NormalizeRelativePath(path, nameof(path)));
    }

    [Fact]
    public void CanonicalizationRejectsInvalidUtf16BeforeEncoding()
    {
        var invalid = "folder/" + new string((char)0xd800, 1) + ".txt";

        Assert.Throws<InvalidDataException>(() =>
            StationPackageCanonicalization.NormalizeRelativePath(invalid, nameof(invalid)));
    }

    [Fact]
    public async Task BuilderRejectsCaseCollidingSourcePathsOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = Path.Combine(_root, "case-collision-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "fixture.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(source, "FIXTURE.txt"), "b");
        using var rsa = RSA.Create(3072);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await BuildPackageAsync(source, "case-collision.olopkg", rsa));

        Assert.Contains("collide", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidPortablePackagePaths => new()
    {
        " leading/file.txt",
        "folder /file.txt",
        "folder/control\u0001.txt",
        "folder/name:.txt",
        "folder/name<.txt",
        "folder/name>.txt",
        "folder/name\".txt",
        "folder/name|.txt",
        "folder/name?.txt",
        "folder/name*.txt",
        "folder/CON",
        "folder/con.txt",
        "folder/PRN.log",
        "folder/AUX",
        "folder/NUL.bin",
        "folder/COM1.log",
        "folder/LPT9",
        "folder/COM\u00b9.txt",
        "folder/e\u0301.txt",
        $"folder/{new string('a', 256)}",
        "../outside.txt",
        "./file.txt",
        "folder/end.",
        "folder\\file.txt",
        "/absolute.txt",
        "folder/"
    };

    [Fact]
    public async Task InstallerRejectsCentralLocalHeaderOffsetMismatch()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "offset-mismatch.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        ClassicCentralRecord record = FindCentralRecord(package, "flows/main.json");
        BinaryPrimitives.WriteUInt32LittleEndian(
            package.AsSpan(record.RecordOffset + 42),
            checked((uint)record.LocalHeaderOffset + 1));
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("local-header offset", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerRejectsCentralLocalFlagsMismatch()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "flags-mismatch.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        ClassicCentralRecord record = FindCentralRecord(package, "flows/main.json");
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(record.LocalHeaderOffset + 6),
            1 << 11);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("headers differ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerRejectsUtf8FlagForAsciiEntryName()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(
            source,
            "ascii-utf8-flag.olopkg",
            rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        ClassicCentralRecord record = FindCentralRecord(package, "flows/main.json");
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(record.RecordOffset + 8),
            1 << 11);
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(record.LocalHeaderOffset + 6),
            1 << 11);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("must omit the UTF-8 ZIP flag", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 21)]
    [InlineData(36, 1)]
    [InlineData(38, 1)]
    public async Task InstallerRejectsNonCanonicalCentralPlatformAttributes(
        int centralFieldOffset,
        ushort replacement)
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(
            source,
            $"central-field-{centralFieldOffset}.olopkg",
            rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        ClassicCentralRecord record = FindCentralRecord(package, "flows/main.json");
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(record.RecordOffset + centralFieldOffset),
            replacement);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.True(
            exception.Message.Contains("canonical", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("unsupported ZIP version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallerBindsEveryEntryTimestampToSignedManifestCreationTime()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "timestamp.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        ClassicCentralRecord record = FindCentralRecord(package, "flows/main.json");
        ushort time = BinaryPrimitives.ReadUInt16LittleEndian(
            package.AsSpan(record.RecordOffset + 12));
        ushort replacement = checked((ushort)(time + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(record.RecordOffset + 12),
            replacement);
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(record.LocalHeaderOffset + 10),
            replacement);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("timestamps", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallerRejectsDataDescriptorFlagInBothHeaders()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "descriptor-flag.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        ClassicCentralRecord record = FindCentralRecord(package, "flows/main.json");
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(record.RecordOffset + 8),
            1 << 3);
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(record.LocalHeaderOffset + 6),
            1 << 3);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("canonical stored", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerRejectsDeflateCompressionEvenWhenContentIsUnchanged()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "deflate.olopkg", rsa);
        using (ZipArchive archive = ZipFile.Open(built.PackagePath, ZipArchiveMode.Update))
        {
            ZipArchiveEntry original = archive.GetEntry("flows/main.json")!;
            DateTimeOffset lastWriteTime = original.LastWriteTime;
            original.Delete();
            ZipArchiveEntry replacement = archive.CreateEntry(
                "flows/main.json",
                CompressionLevel.SmallestSize);
            replacement.LastWriteTime = lastWriteTime;
            await using Stream output = replacement.Open();
            await output.WriteAsync("{\"nodes\":[]}"u8.ToArray());
        }

        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("canonical stored", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerRejectsCentralLocalCrcMismatch()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "crc-mismatch.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        ClassicCentralRecord record = FindCentralRecord(package, "flows/main.json");
        uint localCrc = BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(record.LocalHeaderOffset + 14));
        BinaryPrimitives.WriteUInt32LittleEndian(
            package.AsSpan(record.LocalHeaderOffset + 14),
            localCrc ^ 1);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("headers differ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerRejectsFakeCentralBoundaryGap()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "fake-boundary.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        int footerOffset = FindClassicFooter(package);
        int centralOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(footerOffset + 16)));
        byte[] forged = InsertBytes(
            package,
            centralOffset,
            [0x50, 0x4b, 0x01, 0x02]);
        int forgedFooterOffset = footerOffset + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(
            forged.AsSpan(forgedFooterOffset + 16),
            checked((uint)centralOffset + 4));
        await File.WriteAllBytesAsync(built.PackagePath, forged);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("unique central directory boundary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerRejectsTrailingStoredEntryData()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "compressed-tail.olopkg", rsa);
        await AppendStoredEntryTailAsync(built.PackagePath, "flows/main.json");
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("compressed and expanded sizes differ", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "cache")));
    }

    [Fact]
    public async Task InstallerRejectsMissingZip64LocatorBeforeReadingCentralRecords()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "missing-zip64.olopkg", rsa);
        byte[] package = await File.ReadAllBytesAsync(built.PackagePath);
        int footerOffset = FindClassicFooter(package);
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(footerOffset + 8),
            ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(
            package.AsSpan(footerOffset + 10),
            ushort.MaxValue);
        await File.WriteAllBytesAsync(built.PackagePath, package);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("ZIP64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallerUsesPinnedPackageHandleWhenLinuxPathIsReplaced()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage original = await BuildPackageAsync(source, "pinned.olopkg", rsa);
        await File.WriteAllTextAsync(
            Path.Combine(source, "flows", "main.json"),
            "{\"nodes\":[{\"id\":\"replacement\"}]}");
        BuiltStationPackage replacement = await BuildPackageAsync(source, "replacement.olopkg", rsa);
        var protector = new CallbackContentProtector(() =>
            File.Move(replacement.PackagePath, original.PackagePath, overwrite: true));
        using var installer = new SignedStationPackageInstaller(
            new StationPackageTrustOptions(
                Path.Combine(_root, "cache"),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["factory-signing"] = rsa.ExportSubjectPublicKeyInfoPem()
                },
                ImmutableStationServiceSid:
                    AgentTestStationServiceIdentity.ConfiguredOrFixtureSid()),
            protector);

        InstalledStationPackage installed = await installer.InstallAsync(
            original.PackagePath,
            original.Manifest.ContentSha256);

        Assert.Equal(
            "{\"nodes\":[]}",
            await File.ReadAllTextAsync(Path.Combine(installed.ContentDirectory, "flows", "main.json")));
        Assert.Equal(1, protector.CallbackCount);
    }

    [Fact]
    public async Task InstallerRejectsUntrustedSigningKey()
    {
        var source = CreateSource();
        using var signer = RSA.Create(3072);
        using var other = RSA.Create(3072);
        var packagePath = Path.Combine(_root, "out", "untrusted.olopkg");
        BuiltStationPackage built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
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
        SignedStationPackageInstaller installer = CreateInstaller(other.ExportSubjectPublicKeyInfoPem());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(packagePath, built.Manifest.ContentSha256));

        Assert.Contains("verification failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuilderRejectsRsaPrivateKeyBelowSecurityBaseline()
    {
        var source = CreateSource();
        using var weak = RSA.Create(2048);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
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
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
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

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
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

        BuiltStationPackage built = await SignedStationPackageBuilder.BuildAsync(new BuildStationPackageRequest(
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

    [Fact]
    public async Task InstallerRejectsUnknownCacheEntryBeforeRemovingValidStaging()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "unknown-state.olopkg", rsa);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        var cache = Path.Combine(_root, "cache");
        var staging = Path.Combine(
            cache,
            $".{built.Manifest.ContentSha256}.{Guid.NewGuid():N}.installing");
        var unknown = Path.Combine(cache, "unknown");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "preserve.txt"), "preserve");
        Directory.CreateDirectory(unknown);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(packagePath: built.PackagePath, built.Manifest.ContentSha256));

        Assert.Equal("preserve", File.ReadAllText(Path.Combine(staging, "preserve.txt")));
        Directory.Delete(staging, recursive: true);
        Directory.Delete(unknown);
    }

    [Fact]
    public async Task InstallerRejectsUppercaseTransactionIdentifierAsNonCanonical()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "uppercase-transaction.olopkg", rsa);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        var cache = Path.Combine(_root, "cache");
        var nonCanonical = Path.Combine(
            cache,
            $".{built.Manifest.ContentSha256}.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.installing");
        Directory.CreateDirectory(nonCanonical);
        File.WriteAllText(Path.Combine(nonCanonical, "preserve.txt"), "preserve");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Contains("not a canonical transaction state", error.Message, StringComparison.Ordinal);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(nonCanonical, "preserve.txt")));
        Directory.Delete(nonCanonical, recursive: true);
    }

    [Fact]
    public async Task InstallerPreservesStagingWhenAnOrphanCommitRequiresRecovery()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "orphan-commit.olopkg", rsa);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        var cache = Path.Combine(_root, "cache");
        var staging = Path.Combine(
            cache,
            $".{built.Manifest.ContentSha256}.{Guid.NewGuid():N}.committing");
        var orphanCommit = Path.Combine(cache, $".{built.Manifest.ContentSha256}.installed");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "preserve.txt"), "preserve");
        Directory.CreateDirectory(orphanCommit);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Equal("preserve", File.ReadAllText(Path.Combine(staging, "preserve.txt")));
        Assert.True(Directory.Exists(orphanCommit));
        Directory.Delete(staging, recursive: true);
        Directory.Delete(orphanCommit);
    }

    [Fact]
    public async Task InstallerRejectsExcessiveStagingWithoutDeletingAnyTransaction()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "excessive-staging.olopkg", rsa);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        var cache = Path.Combine(_root, "cache");
        string[] staging = [.. Enumerable.Range(0, 65).Select(_ =>
        {
            var path = Path.Combine(
                cache,
                $".{built.Manifest.ContentSha256}.{Guid.NewGuid():N}.installing");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "preserve.txt"), "preserve");
            return path;
        })];

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.All(staging, path => Assert.True(File.Exists(Path.Combine(path, "preserve.txt"))));
        foreach (var path in staging)
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallerRejectsCanonicalContentOrCommitNameWhenItIsAFile(
        bool commitName)
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "state-file.olopkg", rsa);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        var cache = Path.Combine(_root, "cache");
        var statePath = Path.Combine(
            cache,
            commitName
                ? $".{built.Manifest.ContentSha256}.installed"
                : built.Manifest.ContentSha256);
        File.WriteAllText(statePath, "preserve");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));

        Assert.Equal("preserve", File.ReadAllText(statePath));
        File.Delete(statePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallerRejectsWrongOrExtraCommitMarkerContent(bool addExtraFile)
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "tampered-marker.olopkg", rsa);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        InstalledStationPackage installed = await installer.InstallAsync(
            built.PackagePath,
            built.Manifest.ContentSha256);
        var commitDirectory = CommitDirectoryFor(installed.ContentDirectory);
        var marker = Path.Combine(commitDirectory, "content.sha256");
        if (addExtraFile)
        {
            File.WriteAllText(Path.Combine(commitDirectory, "unexpected.txt"), "unexpected");
        }
        else
        {
            File.SetAttributes(marker, File.GetAttributes(marker) & ~FileAttributes.ReadOnly);
            await File.WriteAllTextAsync(marker, new string('0', 64) + "\n");
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallerValidatesUnrelatedCommitBeforeRemovingStaging(
        bool addExtraFile)
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage committed = await BuildPackageAsync(
            source,
            "committed-state.olopkg",
            rsa);
        File.WriteAllText(Path.Combine(source, "flows", "main.json"), "{\"nodes\":[{\"id\":\"next\"}]}");
        BuiltStationPackage requested = await BuildPackageAsync(
            source,
            "requested-state.olopkg",
            rsa);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        InstalledStationPackage installed = await installer.InstallAsync(
            committed.PackagePath,
            committed.Manifest.ContentSha256);
        var commitDirectory = CommitDirectoryFor(installed.ContentDirectory);
        var marker = Path.Combine(commitDirectory, "content.sha256");
        if (addExtraFile)
        {
            File.WriteAllText(Path.Combine(commitDirectory, "unexpected.txt"), "unexpected");
        }
        else
        {
            File.SetAttributes(marker, File.GetAttributes(marker) & ~FileAttributes.ReadOnly);
            File.WriteAllText(marker, new string('0', 64) + "\n");
        }

        var staging = Path.Combine(
            Path.Combine(_root, "cache"),
            $".{requested.Manifest.ContentSha256}.{Guid.NewGuid():N}.installing");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "preserve.txt"), "preserve");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await installer.InstallAsync(requested.PackagePath, requested.Manifest.ContentSha256));

        Assert.Equal("preserve", File.ReadAllText(Path.Combine(staging, "preserve.txt")));
        Directory.Delete(staging, recursive: true);
    }

    [Fact]
    public async Task InstallerInstancesSerializeSameAndDifferentContentHashes()
    {
        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage first = await BuildPackageAsync(source, "parallel-a.olopkg", rsa);
        File.WriteAllText(Path.Combine(source, "flows", "main.json"), "{\"nodes\":[{\"id\":\"b\"}]}");
        BuiltStationPackage second = await BuildPackageAsync(source, "parallel-b.olopkg", rsa);
        using SignedStationPackageInstaller installerA = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        using SignedStationPackageInstaller installerB = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());

        InstalledStationPackage[] installed = await Task.WhenAll(
            installerA.InstallAsync(first.PackagePath, first.Manifest.ContentSha256).AsTask(),
            installerB.InstallAsync(first.PackagePath, first.Manifest.ContentSha256).AsTask(),
            installerA.InstallAsync(second.PackagePath, second.Manifest.ContentSha256).AsTask(),
            installerB.InstallAsync(second.PackagePath, second.Manifest.ContentSha256).AsTask());

        Assert.Equal(2, installed.Select(item => item.ContentDirectory).Distinct().Count());
        Assert.All(installed, item => Assert.True(Directory.Exists(CommitDirectoryFor(item.ContentDirectory))));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task InstallerRejectsCacheRootReplacementDuringItsLifetime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = CreateSource();
        using var rsa = RSA.Create(3072);
        BuiltStationPackage built = await BuildPackageAsync(source, "replaced-cache.olopkg", rsa);
        using SignedStationPackageInstaller installer = CreateInstaller(
            rsa.ExportSubjectPublicKeyInfoPem());
        var cache = Path.Combine(_root, "cache");
        var original = Path.Combine(_root, "original-cache");
        Directory.Move(cache, original);
        Directory.CreateDirectory(cache);
        try
        {
            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await installer.InstallAsync(built.PackagePath, built.Manifest.ContentSha256));
            Assert.Contains("identity changed", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
        }
        finally
        {
            Directory.Delete(cache);
            Directory.Move(original, cache);
        }
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

        AgentTestStationPackageCache.RemovePackageInstallations(
            cacheRoot,
            new InventoryOnlyTestContentProtector(),
            TestProtectionPolicy());

        Directory.Delete(cacheRoot);
    }

    private static string CommitDirectoryFor(string contentDirectory) => Path.Combine(
        Path.GetDirectoryName(contentDirectory)!,
        $".{Path.GetFileName(contentDirectory)}.installed");

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

    private async Task<BuiltStationPackage> BuildPackageAsync(
        string source,
        string fileName,
        RSA signer) => await SignedStationPackageBuilder.BuildAsync(
        new BuildStationPackageRequest(
            source,
            Path.Combine(_root, "out", fileName),
            "package-line-a",
            "project-a",
            "application-line",
            "snapshot-001",
            "line.main",
            "station.eol",
            "factory-signing",
            signer.ExportRSAPrivateKeyPem(),
            new DateTimeOffset(2026, 7, 11, 8, 0, 0, TimeSpan.Zero)));

    private static int FindClassicFooter(byte[] package)
    {
        const uint footerSignature = 0x06054b50;
        int footerOffset = package.Length - 22;
        if (footerOffset < 0
            || BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(footerOffset))
            != footerSignature
            || BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(footerOffset + 20)) != 0)
        {
            throw new InvalidOperationException("Test package does not have a classic comment-free ZIP footer.");
        }

        return footerOffset;
    }

    private static ClassicCentralRecord FindCentralRecord(
        byte[] package,
        string entryName) => ReadClassicCentralRecords(package)
        .Single(record => string.Equals(record.Name, entryName, StringComparison.Ordinal));

    private static List<ClassicCentralRecord> ReadClassicCentralRecords(
        byte[] package)
    {
        const uint centralSignature = 0x02014b50;
        int footerOffset = FindClassicFooter(package);
        int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(footerOffset + 10));
        int centralOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(footerOffset + 16)));
        var records = new List<ClassicCentralRecord>(entryCount);
        int cursor = centralOffset;
        for (var index = 0; index < entryCount; index++)
        {
            Span<byte> header = package.AsSpan(cursor);
            if (header.Length < 46
                || BinaryPrimitives.ReadUInt32LittleEndian(header) != centralSignature)
            {
                throw new InvalidOperationException("Test package central directory is malformed.");
            }

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            int recordLength = checked(46 + nameLength + extraLength + commentLength);
            string name = Encoding.UTF8.GetString(header.Slice(46, nameLength));
            records.Add(new ClassicCentralRecord(
                cursor,
                checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[42..])),
                BinaryPrimitives.ReadUInt32LittleEndian(header[20..]),
                name,
                recordLength));
            cursor = checked(cursor + recordLength);
        }

        if (cursor != footerOffset)
        {
            throw new InvalidOperationException("Test package central directory is not contiguous.");
        }

        return records;
    }

    private static byte[] InsertBytes(
        byte[] source,
        int offset,
        ReadOnlySpan<byte> inserted)
    {
        var result = new byte[checked(source.Length + inserted.Length)];
        source.AsSpan(0, offset).CopyTo(result);
        inserted.CopyTo(result.AsSpan(offset));
        source.AsSpan(offset).CopyTo(result.AsSpan(offset + inserted.Length));
        return result;
    }

    private static async Task AppendStoredEntryTailAsync(
        string packagePath,
        string entryName)
    {
        byte[] package = await File.ReadAllBytesAsync(packagePath);
        int footerOffset = FindClassicFooter(package);
        int centralOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            package.AsSpan(footerOffset + 16)));
        List<ClassicCentralRecord> records = ReadClassicCentralRecords(package);
        ClassicCentralRecord target = records.Single(record => string.Equals(
            record.Name,
            entryName,
            StringComparison.Ordinal));
        Span<byte> targetLocal = package.AsSpan(target.LocalHeaderOffset);
        int localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(targetLocal[26..]);
        int localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(targetLocal[28..]);
        int dataOffset = checked(target.LocalHeaderOffset + 30 + localNameLength + localExtraLength);
        int insertionOffset = checked(dataOffset + (int)target.CompressedLength);
        byte[] forged = InsertBytes(package, insertionOffset, [0]);

        BinaryPrimitives.WriteUInt32LittleEndian(
            forged.AsSpan(target.LocalHeaderOffset + 18),
            checked(target.CompressedLength + 1));
        foreach (ClassicCentralRecord record in records)
        {
            int forgedRecordOffset = record.RecordOffset + 1;
            if (string.Equals(record.Name, entryName, StringComparison.Ordinal))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    forged.AsSpan(forgedRecordOffset + 20),
                    checked(record.CompressedLength + 1));
            }

            uint localOffset = checked((uint)record.LocalHeaderOffset);
            if (record.LocalHeaderOffset >= insertionOffset)
            {
                localOffset++;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                forged.AsSpan(forgedRecordOffset + 42),
                localOffset);
        }

        int forgedFooterOffset = footerOffset + 1;
        BinaryPrimitives.WriteUInt32LittleEndian(
            forged.AsSpan(forgedFooterOffset + 16),
            checked((uint)centralOffset + 1));
        await File.WriteAllBytesAsync(packagePath, forged);
    }

    private static async Task AppendEntryContentAndForgeAdvertisedLengthAsync(
        string packagePath,
        string entryName,
        int additionalExpandedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(additionalExpandedBytes);
        var replacementPath = packagePath + $".{Guid.NewGuid():N}.malicious";
        var entries = new List<(string Name, byte[] Content, DateTimeOffset LastWriteTime)>();
        long advertisedLength = -1;
        await using (var source = new FileStream(
                         packagePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                await using Stream input = entry.Open();
                using var output = new MemoryStream();
                await input.CopyToAsync(output);
                if (string.Equals(entry.FullName, entryName, StringComparison.Ordinal))
                {
                    advertisedLength = output.Length;
                    output.Write(new byte[additionalExpandedBytes]);
                }

                entries.Add((entry.FullName, output.ToArray(), entry.LastWriteTime));
            }
        }

        if (advertisedLength < 0)
        {
            throw new InvalidOperationException($"ZIP entry '{entryName}' was not found.");
        }

        try
        {
            await using (var replacement = new FileStream(
                             replacementPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(replacement, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach ((string name, byte[] content, DateTimeOffset lastWriteTime) in entries)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                    entry.LastWriteTime = lastWriteTime;
                    await using Stream output = entry.Open();
                    await output.WriteAsync(content);
                }
            }

            await ForgeAdvertisedUncompressedLengthAsync(
                replacementPath,
                entryName,
                advertisedLength);
            File.Move(replacementPath, packagePath, overwrite: true);
        }
        finally
        {
            File.Delete(replacementPath);
        }
    }

    private static async Task ForgeAdvertisedUncompressedLengthAsync(
        string packagePath,
        string entryName,
        long advertisedLength)
    {
        const uint centralDirectoryHeaderSignature = 0x02014b50;
        const uint localFileHeaderSignature = 0x04034b50;
        byte[] archive = await File.ReadAllBytesAsync(packagePath);
        byte[] expectedName = Encoding.UTF8.GetBytes(entryName);
        bool patched = false;
        for (var offset = 0; offset <= archive.Length - 46; offset++)
        {
            Span<byte> header = archive.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != centralDirectoryHeaderSignature)
            {
                continue;
            }

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            int centralRecordLength = checked(46 + nameLength + extraLength + commentLength);
            if (offset > archive.Length - centralRecordLength)
            {
                continue;
            }

            ReadOnlySpan<byte> actualName = header.Slice(46, nameLength);
            if (!actualName.SequenceEqual(expectedName))
            {
                continue;
            }

            uint actualExpandedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            uint forgedLength = checked((uint)advertisedLength);
            if (actualExpandedLength <= forgedLength)
            {
                throw new InvalidOperationException(
                    $"ZIP entry '{entryName}' did not expand beyond its advertised length.");
            }

            int localHeaderOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[42..]));
            Span<byte> localHeader = archive.AsSpan(localHeaderOffset);
            if (localHeader.Length < 30
                || BinaryPrimitives.ReadUInt32LittleEndian(localHeader) != localFileHeaderSignature)
            {
                throw new InvalidOperationException($"ZIP entry '{entryName}' has an invalid local header.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], forgedLength);
            BinaryPrimitives.WriteUInt32LittleEndian(localHeader[22..], forgedLength);
            patched = true;
            break;
        }

        if (!patched)
        {
            throw new InvalidOperationException($"ZIP entry '{entryName}' central directory was not found.");
        }

        await File.WriteAllBytesAsync(packagePath, archive);
    }

    private static ImmutableContentProtectionPolicy TestProtectionPolicy() => new(
        OperatingSystem.IsWindows()
            ? WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.ExternalProgramContentCapabilityName)
            : "unix-reader",
        AgentTestStationServiceIdentity.ConfiguredOrFixtureSid());

    private SignedStationPackageInstaller CreateInstaller(
        string publicKeyPem,
        long maximumPackageBytes = 4L * 1024 * 1024 * 1024) => new(
        new StationPackageTrustOptions(
            Path.Combine(_root, "cache"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["factory-signing"] = publicKeyPem
            },
            ImmutableStationServiceSid:
                AgentTestStationServiceIdentity.ConfiguredOrFixtureSid(),
            MaximumPackageBytes: maximumPackageBytes),
        new InventoryOnlyTestContentProtector(markFilesReadOnly: true));

    private sealed record ClassicCentralRecord(
        int RecordOffset,
        int LocalHeaderOffset,
        uint CompressedLength,
        string Name,
        int RecordLength);

    private sealed class CallbackContentProtector(Action callback) : IImmutableContentProtector
    {
        private readonly InventoryOnlyTestContentProtector _inner = new(markFilesReadOnly: true);
        private int _verifyCount;
        private int _callbackCount;

        public int CallbackCount => Volatile.Read(ref _callbackCount);

        public void ProtectCacheBoundary(
            string cacheRootDirectory,
            ImmutableContentProtectionPolicy policy) =>
            _inner.ProtectCacheBoundary(cacheRootDirectory, policy);

        public void VerifyCacheBoundary(
            string cacheRootDirectory,
            ImmutableContentProtectionPolicy policy)
        {
            _inner.VerifyCacheBoundary(cacheRootDirectory, policy);
            if (Interlocked.Increment(ref _verifyCount) == 2)
            {
                callback();
                _ = Interlocked.Increment(ref _callbackCount);
            }
        }

        public ValueTask ProtectAsync(
            string rootDirectory,
            IReadOnlyCollection<ImmutableContentFile> files,
            ImmutableContentProtectionPolicy policy,
            CancellationToken cancellationToken = default) =>
            _inner.ProtectAsync(rootDirectory, files, policy, cancellationToken);

        public ValueTask VerifyAsync(
            string rootDirectory,
            IReadOnlyCollection<ImmutableContentFile> files,
            ImmutableContentProtectionPolicy policy,
            CancellationToken cancellationToken = default) =>
            _inner.VerifyAsync(rootDirectory, files, policy, cancellationToken);

        public ValueTask VerifyInventoryAsync(
            string rootDirectory,
            IReadOnlyCollection<ImmutableContentFile> files,
            CancellationToken cancellationToken = default) =>
            _inner.VerifyInventoryAsync(rootDirectory, files, cancellationToken);

        public ValueTask RemoveProtectedPackageInstallationAsync(
            string cacheRootDirectory,
            string contentSha256,
            string windowsServiceName,
            ImmutableContentProtectionPolicy policy,
            CancellationToken cancellationToken = default) =>
            _inner.RemoveProtectedPackageInstallationAsync(
                cacheRootDirectory,
                contentSha256,
                windowsServiceName,
                policy,
                cancellationToken);
    }
}
