namespace OpenLineOps.Agent.Infrastructure.Packages;

internal static class StationPackagePath
{
    public static string NormalizeRelative(string path, string parameterName)
    {
        return OpenLineOps.Agent.Contracts.StationPackageCanonicalization.NormalizeRelativePath(
            path,
            parameterName);
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
