using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

internal static class ProjectProcessResourcePath
{
    public static string GetFlowsDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(
                scope.ProjectPath,
                "applications",
                $"application-{ToSafeSegment(scope.ApplicationId)}",
                "flows"));
    }

    public static string GetFlowDirectory(
        ProjectApplicationWorkspaceScope scope,
        string processDefinitionId)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(GetFlowsDirectory(scope), $"process-{ToSafeSegment(processDefinitionId)}"));
    }

    public static string GetFlowPath(
        ProjectApplicationWorkspaceScope scope,
        string processDefinitionId)
    {
        return EnsureInsideProject(scope, Path.Combine(GetFlowDirectory(scope, processDefinitionId), "flow.json"));
    }

    public static string GetCustomBlocksDirectory(ProjectApplicationWorkspaceScope scope)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(
                scope.ProjectPath,
                "applications",
                $"application-{ToSafeSegment(scope.ApplicationId)}",
                "blocks",
                "custom"));
    }

    public static string GetCustomBlockDirectory(
        ProjectApplicationWorkspaceScope scope,
        string blockType)
    {
        return EnsureInsideProject(
            scope,
            Path.Combine(GetCustomBlocksDirectory(scope), $"block-{ToSafeSegment(blockType)}"));
    }

    public static string GetCustomBlockVersionsDirectory(
        ProjectApplicationWorkspaceScope scope,
        string blockType)
    {
        return EnsureInsideProject(
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

        return EnsureInsideProject(
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
        return EnsureInsideProject(
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
        return EnsureInsideProject(
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

        return EnsureInsideProject(
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
        var fullPath = EnsureInsideProject(scope, Path.Combine(flowDirectory, normalizedRelativePath));
        var normalizedFlowDirectory = Path.GetFullPath(flowDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedFlowDirectory, StringComparison.OrdinalIgnoreCase))
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

    private static string EnsureInsideProject(
        ProjectApplicationWorkspaceScope scope,
        string path)
    {
        var projectRoot = Path.GetFullPath(scope.ProjectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Process resource path must stay inside the project directory.");
        }

        return fullPath;
    }
}
