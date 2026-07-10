using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Runner;

public static class RunnerProjectPathResolver
{
    public static string ResolveProjectDirectory(string projectTarget, string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(projectTarget)
                ? projectTarget
                : Path.Combine(currentDirectory, projectTarget));

        if (File.Exists(fullPath))
        {
            if (!IsSupportedProjectFile(fullPath))
            {
                throw new InvalidDataException(
                    $"Project file must use {AutomationProjectFileConvention.ProjectFileExtension} or be named {AutomationProjectFileConvention.LegacyProjectFileName}.");
            }

            return Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("Project manifest path has no parent directory.");
        }

        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        if (IsSupportedProjectFile(fullPath))
        {
            return Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("Project manifest path has no parent directory.");
        }

        return fullPath;
    }

    private static bool IsSupportedProjectFile(string path)
    {
        return path.EndsWith(
                   AutomationProjectFileConvention.ProjectFileExtension,
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   Path.GetFileName(path),
                   AutomationProjectFileConvention.LegacyProjectFileName,
                   StringComparison.OrdinalIgnoreCase);
    }
}
