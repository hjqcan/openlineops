using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenLineOps.ContentProtection;

public static class WindowsContentAccessAuthorizer
{
    public static void GrantReadExecute(string rootDirectory, string readerSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GrantWindows(rootDirectory, readerSid, FileSystemRights.ReadAndExecute);
    }

    public static void GrantWorkspaceModify(string rootDirectory, string readerSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GrantWindows(rootDirectory, readerSid, FileSystemRights.Modify);
    }

    [SupportedOSPlatform("windows")]
    private static void GrantWindows(
        string rootDirectory,
        string readerSid,
        FileSystemRights rights)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(readerSid);
        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(rootDirectory)
            || !Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Content authorization root must be an existing canonical absolute directory.");
        }

        SecurityIdentifier identity;
        try
        {
            identity = new SecurityIdentifier(readerSid);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Content authorization reader SID is invalid.", exception);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(file);
            var info = new FileInfo(file);
            var security = FileSystemAclExtensions.GetAccessControl(info);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                rights,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(info, security);
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .Prepend(root)
                     .OrderByDescending(path => path.Length))
        {
            RejectReparsePoint(directory);
            var info = new DirectoryInfo(directory);
            var security = FileSystemAclExtensions.GetAccessControl(info);
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(info, security);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Content authorization cannot traverse reparse points.");
        }
    }
}
