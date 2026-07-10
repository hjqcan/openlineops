using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

internal static class ProjectProcessResourcePath
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string GetFlowsDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(
                scope.ApplicationRootPath,
                "flows"));
    }

    public static string GetFlowDirectory(
        ProjectApplicationWorkspaceScope scope,
        string processDefinitionId)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(GetFlowsDirectory(scope), $"process-{ToSafeSegment(processDefinitionId)}"));
    }

    public static string GetFlowPath(
        ProjectApplicationWorkspaceScope scope,
        string processDefinitionId)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(GetFlowDirectory(scope, processDefinitionId), "flow.json"));
    }

    public static string GetCustomBlocksDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(
                scope.ApplicationRootPath,
                "blocks",
                "custom"));
    }

    public static string GetCustomBlockDirectory(
        ProjectApplicationWorkspaceScope scope,
        string blockType)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(GetCustomBlocksDirectory(scope), $"block-{ToSafeSegment(blockType)}"));
    }

    public static string GetCustomBlockVersionsDirectory(
        ProjectApplicationWorkspaceScope scope,
        string blockType)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(GetCustomBlockDirectory(scope, blockType), "versions"));
    }

    public static string GetCustomBlockVersionPath(
        ProjectApplicationWorkspaceScope scope,
        string blockType,
        int version)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Blockly block version must be positive.");
        }

        return EnsureInsideApplication(
            scope,
            Path.Combine(
                GetCustomBlockVersionsDirectory(scope, blockType),
                $"version-{version:D6}.json"));
    }

    public static string GetNodeDirectory(
        ProjectApplicationWorkspaceScope scope,
        string processDefinitionId,
        string nodeId)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(
                GetFlowDirectory(scope, processDefinitionId),
                "nodes",
                $"node-{ToSafeSegment(nodeId)}"));
    }

    public static string GetWorkspaceArtifactPath(
        ProjectApplicationWorkspaceScope scope,
        string processDefinitionId,
        string nodeId,
        string sha256)
    {
        return EnsureInsideApplication(
            scope,
            Path.Combine(
                GetNodeDirectory(scope, processDefinitionId, nodeId),
                $"workspace.{sha256}.blockly.json"));
    }

    public static string GetPythonArtifactPath(
        ProjectApplicationWorkspaceScope scope,
        string processDefinitionId,
        string nodeId,
        string editorMode,
        string sha256)
    {
        var prefix = string.Equals(editorMode, "ManualCode", StringComparison.OrdinalIgnoreCase)
            ? "source"
            : "generated";

        return EnsureInsideApplication(
            scope,
            Path.Combine(
                GetNodeDirectory(scope, processDefinitionId, nodeId),
                $"{prefix}.{sha256}.py"));
    }

    public static string ResolveRelativeFile(
        ProjectApplicationWorkspaceScope scope,
        string flowDirectory,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Project process path '{relativePath}' must be relative.");
        }

        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = EnsureInsideApplication(
            scope,
            Path.Combine(flowDirectory, normalizedRelativePath));
        var normalizedFlowDirectory = Path.GetFullPath(flowDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedFlowDirectory, PathComparison))
        {
            throw new InvalidDataException(
                $"Project process path '{relativePath}' escapes its flow directory.");
        }

        return fullPath;
    }

    public static string ToDocumentPath(string flowDirectory, string fullPath)
    {
        return Path.GetRelativePath(flowDirectory, fullPath).Replace('\\', '/');
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
                "Process resource path must stay inside the application directory.");
        }

        return fullPath;
    }
}
