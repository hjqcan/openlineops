namespace OpenLineOps.Application.Abstractions.ProjectWorkspaces;

public static class ProjectWorkspacePathGuard
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static bool EnsureOrdinaryDirectoryOrMissing(string path, string description)
    {
        return EnsureOrdinaryPathOrMissing(
            path,
            description,
            expectDirectory: true);
    }

    public static bool EnsureOrdinaryFileOrMissing(string path, string description)
    {
        return EnsureOrdinaryPathOrMissing(
            path,
            description,
            expectDirectory: false);
    }

    public static string CreateOrdinaryDirectory(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var fullPath = Path.GetFullPath(path);
        EnsureOrdinaryDirectoryOrMissing(fullPath, description);
        Directory.CreateDirectory(fullPath);
        if (!EnsureOrdinaryDirectoryOrMissing(fullPath, description))
        {
            throw new InvalidDataException(
                $"{description} path '{fullPath}' was not created as an ordinary directory.");
        }

        return fullPath;
    }

    private static bool EnsureOrdinaryPathOrMissing(
        string path,
        string description,
        bool expectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var fullPath = Path.GetFullPath(path);
        var candidates = EnumerateFromFileSystemRoot(fullPath);
        var targetExists = false;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var attributes = TryGetExistingAttributes(candidate, description);
            if (attributes is null)
            {
                continue;
            }

            if ((attributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException(
                    $"{description} path entry '{candidate}' must be an ordinary filesystem entry, not a device, symbolic link, or reparse point.");
            }

            var isDirectory = (attributes.Value & FileAttributes.Directory) != 0;
            var isTarget = index == candidates.Count - 1;
            if (isTarget)
            {
                targetExists = true;
            }

            if (!isTarget && !isDirectory)
            {
                throw new InvalidDataException(
                    $"{description} parent path entry '{candidate}' must be an ordinary directory.");
            }

            if (isTarget && isDirectory != expectDirectory)
            {
                var expectedKind = expectDirectory ? "directory" : "file";
                throw new InvalidDataException(
                    $"{description} path '{candidate}' must be an ordinary {expectedKind}.");
            }
        }

        return targetExists;
    }

    private static List<string> EnumerateFromFileSystemRoot(string path)
    {
        var candidates = new List<string>();
        var current = path;
        while (true)
        {
            candidates.Add(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, PathComparison))
            {
                break;
            }

            current = parent;
        }

        candidates.Reverse();
        return candidates;
    }

    private static FileAttributes? TryGetExistingAttributes(
        string path,
        string description)
    {
        if (!FileSystemEntryExists(path, description))
        {
            return null;
        }

        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"{description} path entry '{path}' could not be inspected safely.",
                exception);
        }
    }

    private static bool FileSystemEntryExists(string path, string description)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }

        var parent = Directory.GetParent(path)?.FullName;
        if (parent is null || !Directory.Exists(parent))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        try
        {
            return Directory.EnumerateFileSystemEntries(parent)
                .Any(candidate => string.Equals(
                    Path.GetFullPath(candidate),
                    fullPath,
                    PathComparison));
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"{description} parent directory '{parent}' could not be inspected safely.",
                exception);
        }
    }
}
