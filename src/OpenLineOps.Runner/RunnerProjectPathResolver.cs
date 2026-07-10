using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Runner;

public static class RunnerProjectPathResolver
{
    public static string ResolveProjectTarget(string projectTarget, string currentDirectory)
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
                    $"Project file must use the {AutomationProjectFileConvention.ProjectFileExtension} extension.");
            }

            return fullPath;
        }

        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        if (IsSupportedProjectFile(fullPath))
        {
            return fullPath;
        }

        if (Path.HasExtension(fullPath))
        {
            throw new InvalidDataException(
                $"Project file must use the {AutomationProjectFileConvention.ProjectFileExtension} extension.");
        }

        return fullPath;
    }

    private static bool IsSupportedProjectFile(string path)
    {
        return path.EndsWith(
            AutomationProjectFileConvention.ProjectFileExtension,
            StringComparison.OrdinalIgnoreCase);
    }
}
