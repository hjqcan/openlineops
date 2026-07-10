using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Tests;

public sealed class PluginPackageRuntimeIdentityTests
{
    [Fact]
    public void IsCompleteAcceptsCanonicalLowercaseDigests()
    {
        var identity = CreateIdentity();

        Assert.True(identity.IsComplete);
    }

    [Theory]
    [InlineData("package")]
    [InlineData("manifest")]
    [InlineData("entryAssembly")]
    public void IsCompleteRejectsUppercaseDigestAliases(string digest)
    {
        var identity = CreateIdentity();
        identity = digest switch
        {
            "package" => identity with
            {
                PackageContentSha256 = identity.PackageContentSha256.ToUpperInvariant()
            },
            "manifest" => identity with
            {
                ManifestSha256 = identity.ManifestSha256.ToUpperInvariant()
            },
            "entryAssembly" => identity with
            {
                EntryAssemblySha256 = identity.EntryAssemblySha256.ToUpperInvariant()
            },
            _ => throw new InvalidOperationException($"Unknown digest {digest}.")
        };

        Assert.False(identity.IsComplete);
    }

    private static PluginPackageRuntimeIdentity CreateIdentity()
    {
        return new PluginPackageRuntimeIdentity(
            "plugin.scanner",
            "1.0.0",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "1.0.0",
            "any",
            "openlineops.plugin-abi/1");
    }
}
