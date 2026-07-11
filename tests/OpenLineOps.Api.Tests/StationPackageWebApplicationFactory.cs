using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public class StationPackageWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-api-station-packages-{Guid.NewGuid():N}");
    private readonly string _privateKeyPath;

    public StationPackageWebApplicationFactory()
    {
        var keys = Path.Combine(_root, "keys");
        Directory.CreateDirectory(keys);
        Directory.CreateDirectory(DistributionDirectory);
        Directory.CreateDirectory(DeploymentCatalogDirectory);
        _privateKeyPath = Path.Combine(keys, "release-private.pem");
        using var rsa = RSA.Create(3072);
        File.WriteAllText(_privateKeyPath, rsa.ExportRSAPrivateKeyPem());
    }

    public string DistributionDirectory => Path.Combine(_root, "distribution");

    public string DeploymentCatalogDirectory => Path.Combine(_root, "deployment-catalog");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(
            "OpenLineOps:Projects:StationPackages:DistributionDirectory",
            DistributionDirectory);
        builder.UseSetting(
            "OpenLineOps:Projects:StationPackages:DeploymentCatalogDirectory",
            DeploymentCatalogDirectory);
        builder.UseSetting(
            "OpenLineOps:Projects:StationPackages:SigningKeyId",
            "api-tests-release-signing");
        builder.UseSetting(
            "OpenLineOps:Projects:StationPackages:SigningPrivateKeyPath",
            _privateKeyPath);
        builder.UseSetting(
            "OpenLineOps:Runtime:AgentTransport:DeploymentCatalogDirectory",
            DeploymentCatalogDirectory);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
