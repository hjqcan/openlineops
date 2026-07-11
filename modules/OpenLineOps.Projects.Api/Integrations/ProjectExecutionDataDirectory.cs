using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Api.Integrations;

public static class ProjectExecutionDataDirectory
{
    public static string FromProjectTarget(string projectTarget, string currentDirectory)
    {
        return ForProjectDirectory(ProjectDirectoryFromTarget(projectTarget, currentDirectory));
    }

    public static string ProjectDirectoryFromTarget(string projectTarget, string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        var targetPath = Path.GetFullPath(
            Path.IsPathRooted(projectTarget)
                ? projectTarget
                : Path.Combine(currentDirectory, projectTarget));
        return targetPath.EndsWith(
            AutomationProjectFileConvention.ProjectFileExtension,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
            ? Path.GetDirectoryName(targetPath)
                ?? throw new InvalidDataException("Project file path has no parent directory.")
            : targetPath;
    }

    public static string ForProjectDirectory(string projectDirectory)
    {
        var canonicalDirectory = CanonicalProjectDirectory(projectDirectory);
        var identity = OperatingSystem.IsWindows()
            ? canonicalDirectory.ToUpperInvariant()
            : canonicalDirectory;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenLineOps",
            "Projects",
            digest);
    }

    private static string CanonicalProjectDirectory(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (char.IsWhiteSpace(projectDirectory[0]) || char.IsWhiteSpace(projectDirectory[^1]))
        {
            throw new ArgumentException(
                "Project directory must be a canonical path without boundary whitespace.",
                nameof(projectDirectory));
        }

        var fullPath = Path.GetFullPath(projectDirectory);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, PathComparison)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
