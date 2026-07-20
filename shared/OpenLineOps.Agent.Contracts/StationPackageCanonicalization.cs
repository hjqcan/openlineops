using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.Agent.Contracts;

public static class StationPackageCanonicalization
{
    public const int MaximumRelativePathUtf8Bytes = 1024;

    private const int MaximumSegmentUtf8Bytes = 255;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

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
        string productionLineDefinitionId,
        string stationSystemId,
        IEnumerable<StationPackageEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFramed(hash, "openlineops.station-package-content");
        AppendFramed(hash, Required(projectId, nameof(projectId)));
        AppendFramed(hash, Required(applicationId, nameof(applicationId)));
        AppendFramed(hash, Required(projectSnapshotId, nameof(projectSnapshotId)));
        AppendFramed(
            hash,
            Required(productionLineDefinitionId, nameof(productionLineDefinitionId)));
        AppendFramed(hash, Required(stationSystemId, nameof(stationSystemId)));

        StationPackageEntry[] orderedEntries =
        [
            .. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)
        ];
        AppendInt32(hash, orderedEntries.Length);
        foreach (var entry in orderedEntries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            AppendFramed(hash, NormalizeRelativePath(entry.Path, nameof(entry.Path)));
            AppendInt64(hash, entry.Length);
            AppendFramed(hash, Required(entry.Sha256, nameof(entry.Sha256)));
            AppendFramed(hash, Required(entry.MediaType, nameof(entry.MediaType)));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string NormalizeRelativePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException($"{parameterName} must be a canonical relative package path.");
        }

        int utf8Length;
        try
        {
            utf8Length = StrictUtf8.GetByteCount(path);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"{parameterName} contains invalid Unicode.",
                exception);
        }

        if (path.Contains('\\')
            || path.StartsWith('/')
            || path.EndsWith('/')
            || utf8Length > MaximumRelativePathUtf8Bytes
            || !path.IsNormalized(NormalizationForm.FormC))
        {
            throw new InvalidDataException($"{parameterName} must be a canonical relative package path.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0
            || segment is "." or ".."
            || char.IsWhiteSpace(segment[0])
            || char.IsWhiteSpace(segment[^1])
            || segment.EndsWith('.')
            || StrictUtf8.GetByteCount(segment) > MaximumSegmentUtf8Bytes
            || segment.Any(character => char.IsControl(character)
                || character is ':' or '<' or '>' or '"' or '|' or '?' or '*')
            || IsWindowsReservedSegment(segment)))
        {
            throw new InvalidDataException($"{parameterName} contains an invalid path segment.");
        }

        return string.Join('/', segments);
    }

    public static uint CanonicalDosTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp.Offset != TimeSpan.Zero
            || timestamp.Year is < 1980 or > 2107)
        {
            throw new InvalidDataException(
                "Station package timestamp must be UTC and representable by the canonical DOS timestamp.");
        }

        ushort time = checked((ushort)(
            (timestamp.Hour << 11)
            | (timestamp.Minute << 5)
            | (timestamp.Second / 2)));
        ushort date = checked((ushort)(
            ((timestamp.Year - 1980) << 9)
            | (timestamp.Month << 5)
            | timestamp.Day));
        return ((uint)date << 16) | time;
    }

    private static bool IsWindowsReservedSegment(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length is 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
        }

        return false;
    }

    public static string DeploymentCatalogPath(
        string catalogDirectory,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string stationSystemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFramed(hash, "openlineops.station-package-deployment-catalog");
        AppendFramed(hash, Required(projectId, nameof(projectId)));
        AppendFramed(hash, Required(applicationId, nameof(applicationId)));
        AppendFramed(hash, Required(projectSnapshotId, nameof(projectSnapshotId)));
        AppendFramed(hash, Required(stationSystemId, nameof(stationSystemId)));
        var key = Convert.ToHexStringLower(hash.GetHashAndReset());
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

    private static void AppendFramed(IncrementalHash hash, string value)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("Canonical package identity contains invalid Unicode.", exception);
        }

        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
