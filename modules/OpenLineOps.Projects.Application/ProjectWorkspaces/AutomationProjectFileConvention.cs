using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public static class AutomationProjectFileConvention
{
    public const string ProjectFileExtension = ".oloproj";
    public const string ApplicationProjectFileExtension = ".oloapp";
    public const string LegacyProjectFileName = "openlineops.project.json";
    public const string ApplicationsDirectoryName = "applications";

    private static readonly HashSet<string> WindowsReservedFileNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string GetProjectFileName(string projectId)
    {
        return $"{ToPortableFileStem(projectId, "project")}{ProjectFileExtension}";
    }

    public static string GetApplicationProjectRelativePath(string applicationId)
    {
        var stem = ToPortableFileStem(applicationId, "application");
        return $"{ApplicationsDirectoryName}/{stem}/{stem}{ApplicationProjectFileExtension}";
    }

    public static string GetLegacyApplicationProjectRelativePath(string applicationId)
    {
        var stem = ToPortableFileStem(applicationId, "application");
        return $"{ApplicationsDirectoryName}/application-{ToStableHashedSegment(applicationId)}/{stem}{ApplicationProjectFileExtension}";
    }

    public static string GetApplicationRootRelativePath(string applicationProjectRelativePath)
    {
        ValidateApplicationProjectRelativePath(applicationProjectRelativePath);
        return applicationProjectRelativePath[..applicationProjectRelativePath.LastIndexOf('/')];
    }

    public static string ResolveApplicationProjectPath(
        string projectRootPath,
        string applicationProjectRelativePath)
    {
        ValidateApplicationProjectRelativePath(applicationProjectRelativePath);
        return ResolveProjectRelativePath(projectRootPath, applicationProjectRelativePath);
    }

    public static string ResolveProjectRelativePath(string projectRootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(projectRootPath))
        {
            throw new ArgumentException("Project root path cannot be empty.", nameof(projectRootPath));
        }

        ValidateCanonicalRelativePath(relativePath, nameof(relativePath));

        var root = Path.GetFullPath(projectRootPath.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new InvalidDataException($"Project path '{relativePath}' escapes the project root.");
        }

        return fullPath;
    }

    public static void ValidateApplicationProjectRelativePath(string projectFilePath)
    {
        ValidateCanonicalRelativePath(projectFilePath, nameof(projectFilePath));

        var segments = projectFilePath.Split('/');
        if (segments.Length != 3
            || !string.Equals(segments[0], ApplicationsDirectoryName, StringComparison.Ordinal)
            || !segments[2].EndsWith(ApplicationProjectFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Application project path '{projectFilePath}' must be applications/<folder>/<name>{ApplicationProjectFileExtension}.");
        }
    }

    public static string NormalizeDocumentPath(string path)
    {
        return path
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static void ValidateCanonicalRelativePath(string relativePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\')
            || !string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project path '{relativePath}' must be a canonical forward-slash relative path.");
        }

        var segments = relativePath.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Project path '{relativePath}' contains an invalid segment.");
        }
    }

    private static string ToPortableFileStem(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Project identity cannot be empty.", nameof(value));
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
            readable = fallback;
        }

        var needsHash = !string.Equals(readable, normalized, StringComparison.Ordinal)
            || readable.Length > 64
            || WindowsReservedFileNames.Contains(readable);
        if (!needsHash)
        {
            return readable;
        }

        if (readable.Length > 48)
        {
            readable = readable[..48].TrimEnd('.', '-', '_');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..12];
        return $"{readable}--{hash}";
    }

    private static string ToStableHashedSegment(string value)
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
