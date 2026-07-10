using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

internal static class ProjectEngineeringResourcePath
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string GetWorkspacesDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return GetResourceDirectory(scope, "workspaces");
    }

    public static string GetProjectsDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return GetResourceDirectory(scope, "projects");
    }

    public static string GetRecipesDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return GetResourceDirectory(scope, "recipes");
    }

    public static string GetStationProfilesDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return GetResourceDirectory(scope, "station-profiles");
    }

    public static string GetWorkspacePath(ProjectApplicationWorkspaceScope scope, string workspaceId)
    {
        return GetResourcePath(
            scope,
            GetWorkspacesDirectory(scope),
            ProjectEngineeringResourceKinds.Workspace,
            workspaceId);
    }

    public static string GetProjectPath(ProjectApplicationWorkspaceScope scope, string projectId)
    {
        return GetResourcePath(
            scope,
            GetProjectsDirectory(scope),
            ProjectEngineeringResourceKinds.Project,
            projectId);
    }

    public static string GetRecipePath(ProjectApplicationWorkspaceScope scope, string recipeId)
    {
        return GetResourcePath(
            scope,
            GetRecipesDirectory(scope),
            ProjectEngineeringResourceKinds.Recipe,
            recipeId);
    }

    public static string GetStationProfilePath(
        ProjectApplicationWorkspaceScope scope,
        string stationProfileId)
    {
        return GetResourcePath(
            scope,
            GetStationProfilesDirectory(scope),
            ProjectEngineeringResourceKinds.StationProfile,
            stationProfileId);
    }

    private static string GetResourceDirectory(
        ProjectApplicationWorkspaceScope scope,
        string directoryName)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return EnsureInsideApplication(
            scope,
            Path.Combine(
                scope.ApplicationRootPath,
                "configuration",
                directoryName));
    }

    private static string GetResourcePath(
        ProjectApplicationWorkspaceScope scope,
        string directory,
        string resourceKind,
        string resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new ArgumentException("Engineering resource id cannot be empty.", nameof(resourceId));
        }

        return EnsureInsideApplication(
            scope,
            Path.Combine(directory, $"{resourceKind}-{ToSafeSegment(resourceId)}.json"));
    }

    private static string ToSafeSegment(string value)
    {
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
            readable = "resource";
        }

        if (readable.Length > 64)
        {
            readable = readable[..64];
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
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
                "Engineering configuration path must stay inside the application directory.");
        }

        return fullPath;
    }
}
