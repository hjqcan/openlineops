namespace OpenLineOps.Application.Abstractions.ProjectWorkspaces;

public sealed record ProjectApplicationPluginPackageReference(
    string PluginId,
    string Version,
    string ManifestPath,
    string ContentSha256);

public static class ProjectApplicationPluginPackageReferenceContract
{
    public const string PluginsDirectoryName = "plugins";

    public const string ManifestFileName = "manifest.json";

    public static ProjectApplicationPluginPackageReference[] ValidateAndOrder(
        IEnumerable<ProjectApplicationPluginPackageReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var ordered = references.ToArray();
        var pluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in ordered)
        {
            if (reference is null)
            {
                throw new InvalidDataException("Application plugin package references cannot contain null entries.");
            }

            RequireCanonical(reference.PluginId, nameof(reference.PluginId), 256);
            RequireCanonical(reference.Version, nameof(reference.Version), 128);
            _ = GetPortableId(reference.ManifestPath);
            if (!IsSha256(reference.ContentSha256))
            {
                throw new InvalidDataException(
                    $"Application plugin package '{reference.PluginId}' contentSha256 must be a lowercase 64-character SHA-256 value.");
            }

            if (!pluginIds.Add(reference.PluginId))
            {
                throw new InvalidDataException(
                    $"Application plugin id '{reference.PluginId}' is duplicated or differs only by case.");
            }

            if (!manifestPaths.Add(reference.ManifestPath))
            {
                throw new InvalidDataException(
                    $"Application plugin manifest path '{reference.ManifestPath}' is duplicated or differs only by case.");
            }
        }

        return ordered
            .OrderBy(reference => reference.PluginId, StringComparer.Ordinal)
            .ToArray();
    }

    public static string ManifestPath(string portableId)
    {
        return $"{PluginsDirectoryName}/{PortableId(portableId, nameof(portableId))}/{ManifestFileName}";
    }

    public static string GetPortableId(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)
            || !string.Equals(manifestPath, manifestPath.Trim(), StringComparison.Ordinal)
            || Path.IsPathRooted(manifestPath)
            || manifestPath.Contains('\\')
            || manifestPath.Contains(':')
            || manifestPath.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "Application plugin manifestPath must be canonical forward-slash relative text.");
        }

        var segments = manifestPath.Split('/');
        if (segments.Length != 3
            || !string.Equals(segments[0], PluginsDirectoryName, StringComparison.Ordinal)
            || !string.Equals(segments[2], ManifestFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Application plugin manifestPath must be {PluginsDirectoryName}/<portable-id>/{ManifestFileName}.");
        }

        return PortableId(segments[1], nameof(manifestPath));
    }

    public static string PortableId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 96
            || value is "." or ".."
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException(
                "Plugin portable id must use only ASCII letters, digits, dot, dash, or underscore.",
                parameterName);
        }

        return value;
    }

    public static bool IsSha256(string? value)
    {
        return value is { Length: 64 }
            && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void RequireCanonical(string value, string fieldName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"Application plugin package field '{fieldName}' must be canonical non-empty text.");
        }
    }
}

public interface IProjectApplicationPluginPackageReferenceStore
{
    ValueTask<IReadOnlyCollection<ProjectApplicationPluginPackageReference>> ReadAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    ValueTask ReplaceAsync(
        ProjectApplicationWorkspaceScope scope,
        IReadOnlyCollection<ProjectApplicationPluginPackageReference> references,
        CancellationToken cancellationToken = default);
}
