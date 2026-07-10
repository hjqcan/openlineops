using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

internal static class ProjectTopologyResourcePath
{
    public static string GetTopologyDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(
                scope.ProjectPath,
                "applications",
                $"application-{ToSafeSegment(scope.ApplicationId)}",
                "topology"));
    }

    public static string GetLayoutDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(
                scope.ProjectPath,
                "applications",
                $"application-{ToSafeSegment(scope.ApplicationId)}",
                "layouts"));
    }

    public static string GetTopologyPath(ProjectApplicationWorkspaceScope scope, string topologyId)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(GetTopologyDirectory(scope), $"topology-{ToSafeSegment(topologyId)}.json"));
    }

    public static string GetLayoutPath(ProjectApplicationWorkspaceScope scope, string layoutId)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(GetLayoutDirectory(scope), $"layout-{ToSafeSegment(layoutId)}.json"));
    }

    private static string ToSafeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-');
        }

        var readable = builder.ToString().Trim('.', '-', '_');
        if (string.IsNullOrEmpty(readable))
        {
            readable = "resource";
        }

        if (readable.Length > 64)
        {
            readable = readable[..64];
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))
            .ToLowerInvariant()[..12];

        return $"{readable}--{hash}";
    }

    private static string EnsureInsideProject(
        ProjectApplicationWorkspaceScope scope,
        string path)
    {
        var projectRoot = Path.GetFullPath(scope.ProjectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Topology resource path must stay inside the project directory.");
        }

        return fullPath;
    }
}
