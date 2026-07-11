using System.Text.Json.Serialization;

namespace OpenLineOps.Agent.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationPackageManifest(
    string Format,
    string PackageId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string ContentSha256,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<StationPackageEntry> Entries)
{
    public const string RequiredFormat = "openlineops.station-package";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationPackageEntry(
    string Path,
    long Length,
    string Sha256,
    string MediaType);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StationPackageSignature(
    string Algorithm,
    string KeyId,
    string Signature)
{
    public const string RequiredAlgorithm = "RSA-SHA256";
}
