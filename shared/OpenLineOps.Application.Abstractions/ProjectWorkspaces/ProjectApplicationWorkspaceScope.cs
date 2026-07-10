using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Application.Abstractions.ProjectWorkspaces;

public sealed record ProjectApplicationWorkspaceScope
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public ProjectApplicationWorkspaceScope(
        string projectId,
        string applicationId,
        string projectPath,
        string? applicationProjectRelativePath = null)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(applicationId))
        {
            throw new ArgumentException("Application id cannot be empty.", nameof(applicationId));
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));
        }

        ProjectId = projectId.Trim();
        ApplicationId = applicationId.Trim();
        ProjectPath = Path.GetFullPath(projectPath.Trim());
        ApplicationProjectRelativePath = NormalizeApplicationProjectRelativePath(
            ApplicationId,
            applicationProjectRelativePath);
        ApplicationProjectFilePath = ResolveInsideProject(
            ProjectPath,
            ApplicationProjectRelativePath);
        ApplicationRootPath = Path.GetDirectoryName(ApplicationProjectFilePath)
            ?? throw new InvalidDataException(
                "Application project file must have a parent directory inside the project.");

        RejectExistingReparsePoints(ProjectPath, ApplicationProjectFilePath);
    }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectPath { get; }

    public string ApplicationProjectRelativePath { get; }

    public string ApplicationProjectFilePath { get; }

    public string ApplicationRootPath { get; }

    private static string NormalizeApplicationProjectRelativePath(
        string applicationId,
        string? applicationProjectRelativePath)
    {
        if (applicationProjectRelativePath is null)
        {
            return $"applications/application-{ToLegacySafeSegment(applicationId)}/application.oloapp";
        }

        if (string.IsNullOrWhiteSpace(applicationProjectRelativePath)
            || !string.Equals(
                applicationProjectRelativePath,
                applicationProjectRelativePath.Trim(),
                StringComparison.Ordinal)
            || Path.IsPathRooted(applicationProjectRelativePath)
            || applicationProjectRelativePath.Contains('\\')
            || applicationProjectRelativePath.Contains(':'))
        {
            throw new ArgumentException(
                "Application project path must be a canonical forward-slash relative path.",
                nameof(applicationProjectRelativePath));
        }

        var segments = applicationProjectRelativePath.Split('/');
        if (segments.Length < 2
            || segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment)
                || !string.Equals(segment, segment.Trim(), StringComparison.Ordinal)
                || segment is "." or ".."))
        {
            throw new ArgumentException(
                "Application project path must contain normal relative path segments.",
                nameof(applicationProjectRelativePath));
        }

        if (!string.Equals(
                Path.GetExtension(segments[^1]),
                ".oloapp",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Application project path must reference a .oloapp file.",
                nameof(applicationProjectRelativePath));
        }

        return string.Join('/', segments);
    }

    private static string ResolveInsideProject(string projectPath, string relativePath)
    {
        var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        var projectPrefix = Path.EndsInDirectorySeparator(projectRoot)
            ? projectRoot
            : projectRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(
            projectRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(projectPrefix, PathComparison))
        {
            throw new ArgumentException(
                "Application project path must stay inside the project directory.",
                nameof(relativePath));
        }

        return fullPath;
    }

    private static void RejectExistingReparsePoints(
        string projectPath,
        string applicationProjectFilePath)
    {
        var projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        if (File.Exists(projectRoot))
        {
            throw new InvalidDataException(
                $"Project path '{projectRoot}' is a file, not a directory.");
        }

        RejectReparsePointIfPresent(projectRoot);

        var relativePath = Path.GetRelativePath(projectRoot, applicationProjectFilePath);
        var currentPath = projectRoot;
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);
            if (index < segments.Length - 1 && File.Exists(currentPath))
            {
                throw new InvalidDataException(
                    $"Application project path component '{currentPath}' is a file, not a directory.");
            }

            if (index == segments.Length - 1 && Directory.Exists(currentPath))
            {
                throw new InvalidDataException(
                    $"Application project file path '{currentPath}' is a directory, not a file.");
            }

            RejectReparsePointIfPresent(currentPath);
        }
    }

    private static void RejectReparsePointIfPresent(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Application project path '{path}' is a reparse point and is not supported.");
        }
    }

    private static string ToLegacySafeSegment(string value)
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
}
