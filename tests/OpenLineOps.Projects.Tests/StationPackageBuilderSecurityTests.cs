using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Projects.Infrastructure.Releases;

namespace OpenLineOps.Projects.Tests;

public sealed class StationPackageBuilderSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-package-builder-security-{Guid.NewGuid():N}");

    [Fact]
    public void CanonicalIdentitiesFrameEveryFieldWithoutDelimiterCollisions()
    {
        StationPackageEntry[] entries =
        [
            new(
                "payload.bin",
                1,
                new string('a', 64),
                "application/octet-stream")
        ];

        string first = StationPackageCanonicalization.ComputeContentSha256(
            "project",
            "application\nsnapshot",
            "snapshot-id",
            "line",
            "station",
            entries);
        string second = StationPackageCanonicalization.ComputeContentSha256(
            "project\napplication",
            "snapshot",
            "snapshot-id",
            "line",
            "station",
            entries);
        Assert.NotEqual(first, second);

        string separator = "\u001f";
        string firstCatalog = StationPackageCanonicalization.DeploymentCatalogPath(
            _root,
            "project",
            $"application{separator}snapshot",
            "snapshot-id",
            "station");
        string secondCatalog = StationPackageCanonicalization.DeploymentCatalogPath(
            _root,
            $"project{separator}application",
            "snapshot",
            "snapshot-id",
            "station");
        Assert.NotEqual(firstCatalog, secondCatalog);
    }

    [Fact]
    public async Task PssBaselineAllowsPublicPemAndRejectsPrivatePemAndWeakSigningKey()
    {
        var source = Path.Combine(_root, "source");
        var output = Path.Combine(_root, "output");
        Directory.CreateDirectory(Path.Combine(source, "vendor"));
        await File.WriteAllTextAsync(Path.Combine(source, "release.json"), "{\"frozen\":true}");
        using var signer = RSA.Create(3072);
        await File.WriteAllTextAsync(
            Path.Combine(source, "vendor", "public-cert.pem"),
            signer.ExportSubjectPublicKeyInfoPem());
        var publicPackagePath = Path.Combine(output, "public.olopkg");

        var built = await SignedStationPackageBuilder.BuildAsync(Request(
            source,
            publicPackagePath,
            signer.ExportRSAPrivateKeyPem() + Environment.NewLine));

        Assert.Contains(built.Manifest.Entries, entry => entry.Path == "vendor/public-cert.pem");
        using (var archive = ZipFile.OpenRead(publicPackagePath))
        using (var signatureStream = archive.GetEntry("package.signature.json")!.Open())
        using (var signature = JsonDocument.Parse(signatureStream))
        {
            Assert.Equal(
                "RSA-PSS-SHA256",
                signature.RootElement.GetProperty("algorithm").GetString());
        }

        await File.WriteAllTextAsync(
            Path.Combine(source, "vendor", "secret.pem"),
            signer.ExportRSAPrivateKeyPem());
        var privateKeyError = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                Path.Combine(output, "private.olopkg"),
                signer.ExportRSAPrivateKeyPem())));
        Assert.Contains("private key", privateKeyError.Message, StringComparison.OrdinalIgnoreCase);

        File.Delete(Path.Combine(source, "vendor", "secret.pem"));
        using var weak = RSA.Create(2048);
        var weakKeyError = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                Path.Combine(output, "weak.olopkg"),
                weak.ExportRSAPrivateKeyPem())));
        Assert.Contains("3072", weakKeyError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestHashesAndLengthsComeFromTheBytesWrittenToTheArchive()
    {
        var source = Path.Combine(_root, "single-read-source");
        var output = Path.Combine(_root, "single-read-output", "package.olopkg");
        Directory.CreateDirectory(Path.Combine(source, "flows"));
        await File.WriteAllBytesAsync(
            Path.Combine(source, "flows", "main.bin"),
            Enumerable.Range(0, 4097).Select(value => (byte)(value % 251)).ToArray());
        using var signer = RSA.Create(3072);

        BuiltStationPackage built = await SignedStationPackageBuilder.BuildAsync(Request(
            source,
            output,
            signer.ExportRSAPrivateKeyPem()));

        using ZipArchive archive = ZipFile.OpenRead(output);
        foreach (var manifestEntry in built.Manifest.Entries)
        {
            ZipArchiveEntry archiveEntry = Assert.IsType<ZipArchiveEntry>(
                archive.GetEntry(manifestEntry.Path));
            await using Stream stream = archiveEntry.Open();
            byte[] hash = await SHA256.HashDataAsync(stream);
            Assert.Equal(manifestEntry.Length, archiveEntry.Length);
            Assert.Equal(manifestEntry.Sha256, Convert.ToHexStringLower(hash));
        }
    }

    [Fact]
    public async Task RejectsSourceFileReparsePoint()
    {
        var source = Path.Combine(_root, "reparse-source");
        var output = Path.Combine(_root, "reparse-output", "package.olopkg");
        Directory.CreateDirectory(source);
        var target = Path.Combine(_root, "outside.txt");
        await File.WriteAllTextAsync(target, "outside");
        try
        {
            File.CreateSymbolicLink(Path.Combine(source, "linked.txt"), target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                          or IOException
                                          or PlatformNotSupportedException)
        {
            return;
        }

        using var signer = RSA.Create(3072);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem())));

        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task RejectsSourceHardLinks()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var source = Path.Combine(_root, "hard-link-source");
        var output = Path.Combine(_root, "hard-link-output", "package.olopkg");
        Directory.CreateDirectory(source);
        var original = Path.Combine(source, "original.txt");
        var linked = Path.Combine(source, "linked.txt");
        await File.WriteAllTextAsync(original, "shared");
        bool linkedSuccessfully = OperatingSystem.IsWindows()
            ? CreateHardLink(linked, original, IntPtr.Zero)
            : LinuxCreateHardLink(original, linked) == 0;
        if (!linkedSuccessfully)
        {
            return;
        }

        using var signer = RSA.Create(3072);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem())));

        Assert.Contains("hard link", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsPrivateKeyPemRegardlessOfExtensionOrUtf16Encoding(bool utf16)
    {
        var source = Path.Combine(_root, $"renamed-private-key-{utf16}");
        var output = Path.Combine(_root, $"renamed-private-key-{utf16}.olopkg");
        Directory.CreateDirectory(source);
        using var signer = RSA.Create(3072);
        var path = Path.Combine(source, utf16 ? "plugin.bin" : "plugin.dll");
        if (utf16)
        {
            await File.WriteAllTextAsync(path, signer.ExportRSAPrivateKeyPem(), Encoding.Unicode);
        }
        else
        {
            await File.WriteAllTextAsync(path, signer.ExportRSAPrivateKeyPem(), Encoding.UTF8);
        }

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem())));

        Assert.Contains("private key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("rsa-pkcs8")]
    [InlineData("rsa-pkcs1")]
    [InlineData("ec-pkcs8")]
    [InlineData("ec-sec1")]
    [InlineData("encrypted-pkcs8")]
    public async Task RejectsDerPrivateKeysRegardlessOfExtension(string encoding)
    {
        var source = Path.Combine(_root, $"der-private-key-{encoding}");
        var output = Path.Combine(_root, $"der-private-key-{encoding}.olopkg");
        Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(
            Path.Combine(source, "vendor-plugin.dll"),
            CreateDerPrivateKey(encoding));
        using var signer = RSA.Create(3072);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem())));

        Assert.Contains("private key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task AllowsPublicDerKeyMaterial()
    {
        var source = Path.Combine(_root, "public-der-key");
        var output = Path.Combine(_root, "public-der-key.olopkg");
        Directory.CreateDirectory(source);
        using var signer = RSA.Create(3072);
        await File.WriteAllBytesAsync(
            Path.Combine(source, "vendor-public-key.bin"),
            signer.ExportSubjectPublicKeyInfo());

        BuiltStationPackage built = await SignedStationPackageBuilder.BuildAsync(Request(
            source,
            output,
            signer.ExportRSAPrivateKeyPem()));

        Assert.Single(built.Manifest.Entries);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task RejectsLinuxFifoWithoutWaitingForAWriter()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = Path.Combine(_root, "fifo-source");
        var output = Path.Combine(_root, "fifo-output", "package.olopkg");
        Directory.CreateDirectory(source);
        var fifoPath = Path.Combine(source, "blocked-input.bin");
        Assert.Equal(0, LinuxCreateFifo(fifoPath, 0x180));
        using var signer = RSA.Create(3072);
        Task<BuiltStationPackage> build = SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem()))
            .AsTask();

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await build.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("regular file", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task RejectsSameLengthLinuxSourceMutationDuringCopy()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = Path.Combine(_root, "mutating-source");
        var output = Path.Combine(_root, "mutating-output", "package.olopkg");
        Directory.CreateDirectory(source);
        var payload = Path.Combine(source, "payload.bin");
        await using (var stream = new FileStream(payload, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(64L * 1024 * 1024);
        }

        using var mutationCancellation = new CancellationTokenSource();
        Task mutation = Task.Run(async () =>
        {
            await using var stream = new FileStream(
                payload,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            byte value = 0;
            while (!mutationCancellation.IsCancellationRequested)
            {
                stream.Position = 0;
                await stream.WriteAsync(
                    new byte[] { value++ },
                    mutationCancellation.Token);
                await stream.FlushAsync(mutationCancellation.Token);
                await Task.Yield();
            }
        });

        try
        {
            using var signer = RSA.Create(3072);
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await SignedStationPackageBuilder.BuildAsync(Request(
                    source,
                    output,
                    signer.ExportRSAPrivateKeyPem())));
            Assert.Contains("changed while it was packaged", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(output));
        }
        finally
        {
            await mutationCancellation.CancelAsync();
            try
            {
                await mutation;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task RejectsSourceTreeChangedAfterEnumeration()
    {
        var source = Path.Combine(_root, "changing-tree-source");
        var outputDirectory = Path.Combine(_root, "changing-tree-output");
        var output = Path.Combine(outputDirectory, "package.olopkg");
        Directory.CreateDirectory(source);
        var payload = Path.Combine(source, "payload.bin");
        await using (var stream = new FileStream(payload, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(128L * 1024 * 1024);
        }

        using var signer = RSA.Create(3072);
        Task<BuiltStationPackage> build = SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem()))
            .AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((!Directory.Exists(outputDirectory)
                || !Directory.EnumerateFiles(outputDirectory, "*.tmp").Any())
               && !build.IsCompleted)
        {
            await Task.Delay(1, timeout.Token);
        }

        Assert.False(build.IsCompleted);
        await File.WriteAllTextAsync(Path.Combine(source, "late.txt"), "late", timeout.Token);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await build);

        Assert.Contains("source tree changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task RejectsSourceAlternateDataStreamsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = Path.Combine(_root, "ads-source");
        var output = Path.Combine(_root, "ads-output", "package.olopkg");
        Directory.CreateDirectory(source);
        var payload = Path.Combine(source, "payload.txt");
        await File.WriteAllTextAsync(payload, "payload");
        try
        {
            await File.WriteAllTextAsync(payload + ":hidden", "hidden");
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return;
        }

        using var signer = RSA.Create(3072);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem())));

        Assert.Contains("data stream", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task RejectsSourceFileAboveCanonicalSizeBeforeReadingIt()
    {
        var source = Path.Combine(_root, "oversize-source");
        var output = Path.Combine(_root, "oversize-output", "package.olopkg");
        Directory.CreateDirectory(source);
        var payload = Path.Combine(source, "payload.bin");
        try
        {
            await using var stream = new FileStream(payload, FileMode.CreateNew, FileAccess.Write);
            stream.SetLength(SignedStationPackageBuilder.MaximumContentFileBytes + 1);
        }
        catch (IOException)
        {
            return;
        }

        using var signer = RSA.Create(3072);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem())));

        Assert.Contains("size limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task RejectsSourceAboveCanonicalEntryCountBeforeCreatingArchive()
    {
        var source = Path.Combine(_root, "entry-count-source");
        var output = Path.Combine(_root, "entry-count-output", "package.olopkg");
        Directory.CreateDirectory(source);
        for (var index = 0;
             index <= SignedStationPackageBuilder.MaximumContentEntryCount;
             index++)
        {
            File.Create(Path.Combine(source, $"{index:D5}.bin")).Dispose();
        }

        using var signer = RSA.Create(3072);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SignedStationPackageBuilder.BuildAsync(Request(
                source,
                output,
                signer.ExportRSAPrivateKeyPem())));

        Assert.Contains("entry count", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static BuildStationPackageRequest Request(
        string source,
        string packagePath,
        string privateKeyPem) => new(
        source,
        packagePath,
        "package.security",
        "project.security",
        "application.security",
        "snapshot.security",
        "line.security",
        "station.security",
        "security-key",
        privateKeyPem,
        new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));

    private static byte[] CreateDerPrivateKey(string encoding)
    {
        if (encoding.StartsWith("rsa", StringComparison.Ordinal)
            || string.Equals(encoding, "encrypted-pkcs8", StringComparison.Ordinal))
        {
            using var rsa = RSA.Create(3072);
            return encoding switch
            {
                "rsa-pkcs8" => rsa.ExportPkcs8PrivateKey(),
                "rsa-pkcs1" => rsa.ExportRSAPrivateKey(),
                "encrypted-pkcs8" => rsa.ExportEncryptedPkcs8PrivateKey(
                    "private-key-password",
                    new PbeParameters(
                        PbeEncryptionAlgorithm.Aes256Cbc,
                        HashAlgorithmName.SHA256,
                        10_000)),
                _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
            };
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return encoding switch
        {
            "ec-pkcs8" => ecdsa.ExportPkcs8PrivateKey(),
            "ec-sec1" => ecdsa.ExportECPrivateKey(),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };
    }

    [SupportedOSPlatform("windows")]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [SupportedOSPlatform("linux")]
    [DllImport(
        "libc",
        EntryPoint = "link",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int LinuxCreateHardLink(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [SupportedOSPlatform("linux")]
    [DllImport(
        "libc",
        EntryPoint = "mkfifo",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int LinuxCreateFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);
}
