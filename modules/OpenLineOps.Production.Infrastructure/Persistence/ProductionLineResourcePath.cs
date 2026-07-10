using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Production.Infrastructure.Persistence;

internal static class ProductionLineResourcePath
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string GetLinesDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideApplication(scope, Path.Combine(scope.ApplicationRootPath, "production", "lines"));
    }

    public static string GetLineDirectory(
        ProjectApplicationWorkspaceScope scope,
        string lineDefinitionId)
    {
        return EnsureInsideApplication(scope, Path.Combine(GetLinesDirectory(scope), lineDefinitionId));
    }

    public static string GetLinePath(
        ProjectApplicationWorkspaceScope scope,
        string lineDefinitionId)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(GetLineDirectory(scope, lineDefinitionId), "line.json"));
    }

    private static string EnsureInsideApplication(
        ProjectApplicationWorkspaceScope scope,
        string path)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var applicationRoot = Path.GetFullPath(scope.ApplicationRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var applicationPrefix = applicationRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(applicationPrefix, PathComparison))
        {
            throw new InvalidOperationException(
                "Production line resource path must stay inside the portable Application.");
        }

        return fullPath;
    }
}
