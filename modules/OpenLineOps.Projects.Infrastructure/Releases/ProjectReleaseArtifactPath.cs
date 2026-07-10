using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Projects.Infrastructure.Releases;

internal static class ProjectReleaseArtifactPath
{
    private const string ReleasesDirectoryName = "releases";
    private const string StagingDirectoryName = ".staging";
    private const string SourceDirectoryName = "source";
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string GetSourceApplicationPath(ProjectApplicationWorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return EnsureInsideProject(scope, scope.ApplicationRootPath);
    }

    public static string GetSourceApplicationRelativePath(ProjectApplicationWorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.ApplicationProjectRelativePath[..scope.ApplicationProjectRelativePath.LastIndexOf('/')];
    }

    public static string GetReleasesPath(ProjectApplicationWorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return EnsureInsideProject(scope, Path.Combine(scope.ProjectPath, ReleasesDirectoryName));
    }

    public static string GetReleaseRootPath(ProjectApplicationWorkspaceScope scope, string snapshotId)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(
                GetReleasesPath(scope),
                $"release-{ToSafeSegment(snapshotId, nameof(snapshotId))}"));
    }

    public static string GetStagingRootPath(ProjectApplicationWorkspaceScope scope, string snapshotId)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(
                GetReleasesPath(scope),
                StagingDirectoryName,
                $"release-{ToSafeSegment(snapshotId, nameof(snapshotId))}-{Guid.NewGuid():N}"));
    }

    public static string GetSourceRootPath(string releaseRootPath)
    {
        return Path.Combine(releaseRootPath, SourceDirectoryName);
    }

    public static string GetManifestPath(string releaseRootPath)
    {
        return Path.Combine(releaseRootPath, ProjectReleaseArtifactManifest.FileName);
    }

    public static string ResolveRelativePath(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\'))
        {
            throw new InvalidDataException($"Release path '{relativePath}' must be a forward-slash relative path.");
        }

        var root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(rootPrefix, PathComparison))
        {
            throw new InvalidDataException($"Release path '{relativePath}' escapes its root directory.");
        }

        return fullPath;
    }

    public static string NormalizeDocumentPath(string path)
    {
        return path
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    public static string GetDocumentPath(string rootPath, string fullPath)
    {
        return NormalizeDocumentPath(Path.GetRelativePath(rootPath, fullPath));
    }

    public static void EnsureStagingPath(string releasesPath, string stagingRootPath)
    {
        var stagingRoot = Path.GetFullPath(Path.Combine(releasesPath, StagingDirectoryName))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stagingPrefix = stagingRoot + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(stagingRootPath);

        if (!candidate.StartsWith(stagingPrefix, PathComparison))
        {
            throw new InvalidOperationException("Release staging path must stay inside the releases staging directory.");
        }
    }

    private static string ToSafeSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Release identity cannot be empty.", parameterName);
        }

        var normalized = value.Trim();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-');
        }

        var readable = builder.ToString().Trim('.', '-', '_');
        if (string.IsNullOrEmpty(readable))
        {
            readable = "release";
        }

        if (readable.Length > 64)
        {
            readable = readable[..64];
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..12];
        return $"{readable}--{hash}";
    }

    private static string EnsureInsideProject(ProjectApplicationWorkspaceScope scope, string path)
    {
        var projectRoot = Path.GetFullPath(scope.ProjectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectPrefix = projectRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(projectPrefix, PathComparison))
        {
            throw new InvalidOperationException("Release path must stay inside the project directory.");
        }

        return fullPath;
    }
}
