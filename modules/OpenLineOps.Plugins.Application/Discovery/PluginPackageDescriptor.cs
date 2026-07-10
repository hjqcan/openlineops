using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Application.Discovery;

public sealed record PluginPackageDescriptor(
    PluginManifest Manifest,
    string PackagePath,
    string ManifestPath,
    string PackageContentSha256 = "",
    string ManifestSha256 = "",
    string EntryAssemblySha256 = "",
    string ManifestRelativePath = "",
    string EntryAssemblyRelativePath = "",
    IReadOnlyCollection<PluginPackageFileDescriptor>? Files = null)
{
    public PluginPackageRuntimeIdentity RuntimeIdentity => new(
        Manifest.Id,
        Manifest.Version,
        PackageContentSha256,
        ManifestSha256,
        EntryAssemblySha256,
        Manifest.ContractVersion,
        Manifest.RuntimeIdentifier,
        Manifest.AbiVersion);
}

public sealed record PluginPackageFileDescriptor(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record PluginPackageRuntimeIdentity(
    string PluginId,
    string Version,
    string PackageContentSha256,
    string ManifestSha256,
    string EntryAssemblySha256,
    string ContractVersion,
    string RuntimeIdentifier,
    string AbiVersion)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(PluginId)
        && !string.IsNullOrWhiteSpace(Version)
        && IsSha256(PackageContentSha256)
        && IsSha256(ManifestSha256)
        && IsSha256(EntryAssemblySha256)
        && !string.IsNullOrWhiteSpace(ContractVersion)
        && !string.IsNullOrWhiteSpace(RuntimeIdentifier)
        && !string.IsNullOrWhiteSpace(AbiVersion);

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }
}
