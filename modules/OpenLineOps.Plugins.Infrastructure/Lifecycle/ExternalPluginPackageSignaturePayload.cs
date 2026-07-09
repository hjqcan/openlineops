using System.Globalization;
using OpenLineOps.Plugins.Application.Discovery;

namespace OpenLineOps.Plugins.Infrastructure.Lifecycle;

public static class ExternalPluginPackageSignaturePayload
{
    public const string FormatVersion = "OpenLineOps.PluginPackageSignature.v1";

    public static string Create(
        PluginPackageDescriptor package,
        string entryAssemblyPath,
        string entryAssemblySha256,
        string manifestSha256)
    {
        ArgumentNullException.ThrowIfNull(package);

        return string.Join(
            "\n",
            FormatVersion,
            $"contractVersion={package.Manifest.ContractVersion}",
            $"entryAssembly={package.Manifest.EntryAssembly}",
            $"entryAssemblyPath={Path.GetFileName(entryAssemblyPath)}",
            $"entryAssemblySha256={NormalizeHash(entryAssemblySha256)}",
            $"entryType={package.Manifest.EntryType}",
            $"id={package.Manifest.Id}",
            $"kind={package.Manifest.Kind.ToString()}",
            $"manifestFile={Path.GetFileName(package.ManifestPath)}",
            $"manifestSha256={NormalizeHash(manifestSha256)}",
            $"minimumPlatformVersion={package.Manifest.MinimumPlatformVersion}",
            $"version={package.Manifest.Version}");
    }

    private static string NormalizeHash(string hash)
    {
        return hash
            .Replace(":", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Trim()
            .ToUpper(CultureInfo.InvariantCulture);
    }
}
