using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

internal static class ProjectTopologyResourcePath
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string GetTopologyDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(
                scope.ApplicationRootPath,
                "topology"));
    }

    public static string GetLayoutDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(
                scope.ApplicationRootPath,
                "layouts"));
    }

    public static string GetTopologyPath(ProjectApplicationWorkspaceScope scope, string topologyId)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(GetTopologyDirectory(scope), $"topology-{ToSafeSegment(topologyId)}.json"));
    }

    public static string GetLayoutPath(ProjectApplicationWorkspaceScope scope, string layoutId)
    {
        return EnsureInsideApplication(
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

    private static string EnsureInsideApplication(
        ProjectApplicationWorkspaceScope scope,
        string path)
    {
        var applicationRoot = Path.GetFullPath(scope.ApplicationRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(applicationRoot, PathComparison))
        {
            throw new InvalidOperationException(
                "Topology resource path must stay inside the application directory.");
        }

        return fullPath;
    }
}
