using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.Agent.Contracts;

public static class StationPackageCanonicalization
{
    public static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };

    public static string ComputeContentSha256(
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string stationSystemId,
        IEnumerable<StationPackageEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Required(projectId, nameof(projectId)));
        Append(hash, "\n");
        Append(hash, Required(applicationId, nameof(applicationId)));
        Append(hash, "\n");
        Append(hash, Required(projectSnapshotId, nameof(projectSnapshotId)));
        Append(hash, "\n");
        Append(hash, Required(stationSystemId, nameof(stationSystemId)));
        Append(hash, "\n");
        foreach (var entry in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            Append(hash, NormalizeRelativePath(entry.Path, nameof(entry.Path)));
            Append(hash, "\n");
            Append(hash, entry.Length.ToString(CultureInfo.InvariantCulture));
            Append(hash, "\n");
            Append(hash, entry.Sha256);
            Append(hash, "\n");
            Append(hash, entry.MediaType);
            Append(hash, "\n");
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string NormalizeRelativePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Contains('\\')
            || path.StartsWith('/')
            || path.EndsWith('/'))
        {
            throw new InvalidDataException($"{parameterName} must be a canonical relative package path.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0
            || segment is "." or ".."
            || segment.EndsWith(' ')
            || segment.EndsWith('.')))
        {
            throw new InvalidDataException($"{parameterName} contains an invalid path segment.");
        }

        return string.Join('/', segments);
    }

    public static string DeploymentCatalogPath(
        string catalogDirectory,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string stationSystemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        var identity = string.Join(
            '\u001f',
            Required(projectId, nameof(projectId)),
            Required(applicationId, nameof(applicationId)),
            Required(projectSnapshotId, nameof(projectSnapshotId)),
            Required(stationSystemId, nameof(stationSystemId)));
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Path.GetFullPath(catalogDirectory), $"{key}.json");
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1])
            ? throw new ArgumentException(
                $"{parameterName} must be canonical non-empty text.",
                parameterName)
            : value;

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}
