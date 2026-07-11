using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Projects.Infrastructure.Releases;

namespace OpenLineOps.Projects.Tests;

public sealed class StationPackageBuilderSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-package-builder-security-{Guid.NewGuid():N}");

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
}
