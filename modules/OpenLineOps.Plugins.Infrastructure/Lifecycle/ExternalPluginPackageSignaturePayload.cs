using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public static class ExternalPluginPackageSignaturePayload
{
    public const string PayloadIdentity = "OpenLineOps.PluginPackageSignature";

    public static string Create(
        PluginPackageDescriptor package,
        string entryAssemblyPath,
        string entryAssemblySha256,
        string manifestSha256)
    {
        ArgumentNullException.ThrowIfNull(package);

        return string.Join(
            "\n",
            PayloadIdentity,
            $"contractVersion={package.Manifest.ContractVersion}",
            $"entryAssembly={package.Manifest.EntryAssembly}",
            $"entryAssemblyPath={Path.GetFileName(entryAssemblyPath)}",
            $"entryAssemblySha256={RequireCanonicalSha256(entryAssemblySha256, nameof(entryAssemblySha256))}",
            $"entryType={package.Manifest.EntryType}",
            $"id={package.Manifest.Id}",
            $"kind={package.Manifest.Kind.ToString()}",
            $"manifestFile={Path.GetFileName(package.ManifestPath)}",
            $"manifestSha256={RequireCanonicalSha256(manifestSha256, nameof(manifestSha256))}",
            $"minimumPlatformVersion={package.Manifest.MinimumPlatformVersion}",
            $"version={package.Manifest.Version}");
    }

    private static string RequireCanonicalSha256(string hash, string parameterName)
    {
        if (hash.Length != 64
            || !string.Equals(hash, hash.ToLowerInvariant(), StringComparison.Ordinal)
            || !hash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "SHA-256 must be exactly 64 lowercase hexadecimal characters.",
                parameterName);
        }

        return hash;
    }
}
