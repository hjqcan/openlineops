using Microsoft.Extensions.Configuration;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Api.HostedServices;

public sealed class ProjectsStartupWorkspaceOptions
{
    public const string SectionName = "OpenLineOps:Projects:StartupWorkspaces";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private ProjectsStartupWorkspaceOptions(IReadOnlyList<string> projectFiles)
    {
        ProjectFiles = projectFiles;
    }

    public IReadOnlyList<string> ProjectFiles { get; }

    public static ProjectsStartupWorkspaceOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        if (!section.Exists())
        {
            return new ProjectsStartupWorkspaceOptions([]);
        }

        var unknownSetting = section.GetChildren().FirstOrDefault(child =>
            !string.Equals(child.Key, nameof(ProjectFiles), StringComparison.Ordinal));
        if (unknownSetting is not null)
        {
            throw new InvalidOperationException(
                $"Unsupported startup workspace setting '{unknownSetting.Path}'.");
        }

        var projectFilesSection = section.GetSection(nameof(ProjectFiles));
        var entries = projectFilesSection.GetChildren().ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ProjectFiles)} must contain at least one absolute "
                + $"{AutomationProjectFileConvention.ProjectFileExtension} file when the section is configured.");
        }

        var projectFiles = new List<string>(entries.Length);
        var uniquePaths = new HashSet<string>(PathComparer);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (!string.Equals(
                    entry.Key,
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || entry.GetChildren().Any())
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(ProjectFiles)} must be one contiguous scalar array.");
            }

            var configuredPath = entry.Value;
            if (string.IsNullOrWhiteSpace(configuredPath)
                || !string.Equals(configuredPath, configuredPath.Trim(), StringComparison.Ordinal)
                || !Path.IsPathFullyQualified(configuredPath)
                || !string.Equals(
                    Path.GetExtension(configuredPath),
                    AutomationProjectFileConvention.ProjectFileExtension,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{entry.Path} must be a canonical absolute "
                    + $"{AutomationProjectFileConvention.ProjectFileExtension} file path.");
            }

            var fullPath = Path.GetFullPath(configuredPath);
            if (!PathComparer.Equals(fullPath, configuredPath)
                || !uniquePaths.Add(fullPath))
            {
                throw new InvalidOperationException(
                    $"{entry.Path} must be a unique canonical absolute Project file path.");
            }

            projectFiles.Add(fullPath);
        }

        return new ProjectsStartupWorkspaceOptions(projectFiles);
    }
}
