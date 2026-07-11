namespace OpenLineOps.Agent.Infrastructure.Packages;

internal static class StationPackagePath
{
    public static string NormalizeRelative(string path, string parameterName)
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

    public static string FromFile(string rootPath, string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
        return NormalizeRelative(relative, nameof(filePath));
    }

    public static string ResolveInside(string rootPath, string relativePath)
    {
        var normalized = NormalizeRelative(relativePath, nameof(relativePath));
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(
            Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Package path '{relativePath}' escapes its root.");
        }

        return resolved;
    }
}
