using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace OpenLineOps.ContentProtection;

public sealed record ImmutableContentFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ImmutableContentProtectionPolicy(
    string ReaderSid,
    string? HostReaderSid = null)
{
    [SupportedOSPlatform("windows")]
    public IReadOnlyCollection<SecurityIdentifier> ResolveReaderSids()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows content protection identities require Windows.");
        }

        var values = HostReaderSid is null
            ? new[] { ReaderSid }
            : new[] { ReaderSid, HostReaderSid };
        var readers = new List<SecurityIdentifier>(values.Length);
        foreach (var value in values)
        {
            SecurityIdentifier reader;
            try
            {
                reader = new SecurityIdentifier(value);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Immutable content reader SID is invalid.", exception);
            }

            if (reader.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || reader.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            {
                throw new InvalidDataException(
                    "Immutable content reader must be a dedicated non-administrative identity.");
            }

            if (!readers.Contains(reader))
            {
                readers.Add(reader);
            }
        }

        return readers;
    }
}

public interface IImmutableContentProtector
{
    ValueTask ProtectAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default);

    ValueTask VerifyAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default);

    ValueTask VerifyInventoryAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        CancellationToken cancellationToken = default);

    void DeleteProtectedInstallation(string cacheRootDirectory, string contentDirectory);
}

public sealed class ImmutableContentProtector : IImmutableContentProtector
{
    private const int BufferSize = 64 * 1024;

    public async ValueTask ProtectAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var root = ResolveRoot(rootDirectory);
        var inventory = ValidateInventory(root, files);
        await VerifyInventoryCoreAsync(root, inventory, cancellationToken).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            ProtectWindows(root, inventory, policy.ResolveReaderSids());
        }
        else
        {
            ProtectUnix(root, inventory);
        }

        await VerifyAsync(root, inventory, policy, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask VerifyAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var root = ResolveRoot(rootDirectory);
        var inventory = ValidateInventory(root, files);
        await VerifyInventoryCoreAsync(root, inventory, cancellationToken).ConfigureAwait(false);

        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsProtection(root, inventory, policy.ResolveReaderSids());
        }
        else
        {
            VerifyUnixProtection(root, inventory);
        }
    }

    public ValueTask VerifyInventoryAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(rootDirectory);
        var inventory = ValidateInventory(root, files);
        return VerifyInventoryCoreAsync(root, inventory, cancellationToken);
    }

    public void DeleteProtectedInstallation(string cacheRootDirectory, string contentDirectory)
    {
        var cacheRoot = Path.GetFullPath(cacheRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var content = Path.GetFullPath(contentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!content.StartsWith(cacheRoot + Path.DirectorySeparatorChar, comparison)
            || !string.Equals(Path.GetDirectoryName(content), cacheRoot, comparison)
            || (File.GetAttributes(content) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Protected content cleanup is restricted to one direct cache entry.");
        }

        var leaf = Path.GetFileName(content);
        if (!IsSha256(leaf)
            && !(leaf.Length > 0
                 && leaf[0] == '.'
                 && leaf.EndsWith(".installing", StringComparison.Ordinal)
                 && leaf.Count(character => character == '.') == 3))
        {
            throw new InvalidDataException(
                "Protected content cleanup requires a content-addressed installation path.");
        }

        if (!Directory.Exists(content))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            GrantCurrentIdentityFullControl(content);
        }
        else
        {
            RestoreUnixWriteAccess(content);
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     content,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(content, File.GetAttributes(content) & ~FileAttributes.ReadOnly);
        Directory.Delete(content, recursive: true);
    }

    private static string ResolveRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(rootDirectory)
            || !string.Equals(
                root,
                rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            || !Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Immutable content root must be an existing canonical absolute directory without reparse points.");
        }

        return root;
    }

    private static IReadOnlyCollection<ImmutableContentFile> ValidateInventory(
        string root,
        IReadOnlyCollection<ImmutableContentFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new InvalidDataException("Immutable content inventory cannot be empty.");
        }

        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (file.SizeBytes < 0
                || !IsSha256(file.Sha256)
                || !IsCanonicalRelativePath(file.RelativePath)
                || !paths.Add(file.RelativePath))
            {
                throw new InvalidDataException("Immutable content inventory is invalid or duplicated.");
            }

            _ = ResolveFile(root, file.RelativePath);
        }

        return files;
    }

    private static async ValueTask VerifyInventoryCoreAsync(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory,
        CancellationToken cancellationToken)
    {
        var expectedFiles = inventory
            .Select(file => file.RelativePath)
            .ToHashSet(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var expectedDirectories = inventory
            .SelectMany(file => ParentDirectories(file.RelativePath))
            .ToHashSet(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var actualFiles = new HashSet<string>(expectedFiles.Comparer);
        var actualDirectories = new HashSet<string>(expectedDirectories.Comparer);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Immutable content cannot contain reparse points.");
            }

            var relative = PortableRelativePath(root, path);
            if (Directory.Exists(path))
            {
                actualDirectories.Add(relative);
            }
            else
            {
                actualFiles.Add(relative);
            }
        }

        if (!actualFiles.SetEquals(expectedFiles)
            || !actualDirectories.SetEquals(expectedDirectories))
        {
            throw new InvalidDataException(
                "Immutable content directory differs from its complete file inventory.");
        }

        foreach (var file in inventory)
        {
            var path = ResolveFile(root, file.RelativePath);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.SizeBytes)
            {
                throw new InvalidDataException(
                    $"Immutable content file '{file.RelativePath}' has an unexpected size.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Immutable content file '{file.RelativePath}' failed SHA-256 verification.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindows(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory,
        IReadOnlyCollection<SecurityIdentifier> readers)
    {
        var files = inventory.Select(file => ResolveFile(root, file.RelativePath)).ToArray();
        var directories = files
            .Select(Path.GetDirectoryName)
            .Where(path => path is not null && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ToArray();
        foreach (var file in files)
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            ApplyFileProtection(file, readers);
        }

        foreach (var directory in directories)
        {
            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);
            ApplyDirectoryProtection(directory, readers);
        }

        File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.ReadOnly);
        ApplyDirectoryProtection(root, readers);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsProtection(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory,
        IReadOnlyCollection<SecurityIdentifier> readers)
    {
        foreach (var file in inventory)
        {
            var path = ResolveFile(root, file.RelativePath);
            VerifyFileSystemSecurity(FileSystemAclExtensions.GetAccessControl(new FileInfo(path)), readers);
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) == 0)
            {
                throw new InvalidDataException(
                    $"Immutable content file '{file.RelativePath}' is not read-only.");
            }
        }

        foreach (var directory in inventory
                     .Select(file => Path.GetDirectoryName(ResolveFile(root, file.RelativePath)))
                     .Append(root)
                     .Where(path => path is not null)
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            VerifyFileSystemSecurity(
                FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory)),
                readers);
            if ((File.GetAttributes(directory) & FileAttributes.ReadOnly) == 0)
            {
                throw new InvalidDataException(
                    $"Immutable content directory '{directory}' is not read-only.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyFileProtection(
        string path,
        IReadOnlyCollection<SecurityIdentifier> readers)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRules(security, readers, InheritanceFlags.None);
        FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyDirectoryProtection(
        string path,
        IReadOnlyCollection<SecurityIdentifier> readers)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRules(
            security,
            readers,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
    }

    [SupportedOSPlatform("windows")]
    private static void AddRules(
        FileSystemSecurity security,
        IReadOnlyCollection<SecurityIdentifier> readers,
        InheritanceFlags inheritanceFlags)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            inheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            inheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Allow));
        foreach (var reader in readers)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                reader,
                FileSystemRights.ReadAndExecute,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyFileSystemSecurity(
        FileSystemSecurity security,
        IReadOnlyCollection<SecurityIdentifier> readers)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidDataException("Immutable content ACL inheritance must be disabled.");
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != 2 + readers.Count
            || rules.Any(rule => rule.IsInherited || rule.AccessControlType != AccessControlType.Allow)
            || !HasRights(rules, system, FileSystemRights.FullControl)
            || !HasRights(rules, administrators, FileSystemRights.FullControl)
            || readers.Any(reader => !HasReaderRights(rules, reader)))
        {
            throw new InvalidDataException(
                "Immutable content ACL must grant only SYSTEM and Administrators full control and the declared readers read/execute.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasRights(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        FileSystemRights rights) => rules.Any(rule =>
        identity.Equals(rule.IdentityReference)
        && (rule.FileSystemRights & rights) == rights);

    [SupportedOSPlatform("windows")]
    private static bool HasReaderRights(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier reader)
    {
        const FileSystemRights forbidden = FileSystemRights.Write
                                           | FileSystemRights.Delete
                                           | FileSystemRights.DeleteSubdirectoriesAndFiles
                                           | FileSystemRights.ChangePermissions
                                           | FileSystemRights.TakeOwnership;
        return rules.Any(rule =>
            reader.Equals(rule.IdentityReference)
            && (rule.FileSystemRights & FileSystemRights.ReadAndExecute)
            == FileSystemRights.ReadAndExecute
            && (rule.FileSystemRights & forbidden) == 0);
    }

    [UnsupportedOSPlatform("windows")]
    private static void ProtectUnix(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory)
    {
        foreach (var file in inventory)
        {
            var path = ResolveFile(root, file.RelativePath);
            var prior = File.GetUnixFileMode(path);
            var executable = (prior & (UnixFileMode.UserExecute
                                       | UnixFileMode.GroupExecute
                                       | UnixFileMode.OtherExecute)) != 0;
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.GroupRead
                | UnixFileMode.OtherRead
                | (executable
                    ? UnixFileMode.UserExecute
                      | UnixFileMode.GroupExecute
                      | UnixFileMode.OtherExecute
                    : 0));
        }

        foreach (var directory in inventory
                     .Select(file => Path.GetDirectoryName(ResolveFile(root, file.RelativePath)))
                     .Append(root)
                     .Where(path => path is not null)
                     .Cast<string>()
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(path => path.Length))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void VerifyUnixProtection(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory)
    {
        const UnixFileMode writeBits = UnixFileMode.UserWrite
                                       | UnixFileMode.GroupWrite
                                       | UnixFileMode.OtherWrite;
        foreach (var file in inventory)
        {
            if ((File.GetUnixFileMode(ResolveFile(root, file.RelativePath)) & writeBits) != 0)
            {
                throw new InvalidDataException(
                    $"Immutable content file '{file.RelativePath}' is writable.");
            }
        }

        foreach (var directory in inventory
                     .Select(file => Path.GetDirectoryName(ResolveFile(root, file.RelativePath)))
                     .Append(root)
                     .Where(path => path is not null)
                     .Cast<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            if ((File.GetUnixFileMode(directory) & writeBits) != 0)
            {
                throw new InvalidDataException(
                    $"Immutable content directory '{directory}' is writable.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantCurrentIdentityFullControl(string root)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var current = identity.User
                      ?? throw new InvalidOperationException("Current Windows identity has no SID.");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var security = FileSystemAclExtensions.GetAccessControl(new FileInfo(file));
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(file), security);
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .Prepend(root)
                     .OrderBy(path => path.Length))
        {
            var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory));
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(directory), security);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void RestoreUnixWriteAccess(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserWrite);
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .Prepend(root))
        {
            File.SetUnixFileMode(
                directory,
                File.GetUnixFileMode(directory)
                | UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }

    private static string ResolveFile(string root, string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidDataException("Immutable content path escapes its root.");
        }

        return resolved;
    }

    private static IEnumerable<string> ParentDirectories(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var length = 1; length < segments.Length; length++)
        {
            yield return string.Join('/', segments.Take(length));
        }
    }

    private static string PortableRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsCanonicalRelativePath(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathRooted(value)
        && !value.Contains('\\')
        && value.Split('/').All(segment =>
            segment.Length > 0
            && segment is not "." and not ".."
            && !char.IsWhiteSpace(segment[0])
            && !char.IsWhiteSpace(segment[^1]));

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
