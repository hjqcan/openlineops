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
    internal WindowsContentProtectionIdentities ResolveWindowsIdentities()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows content protection identities require Windows.");
        }

        var externalReader = ParseSid(ReaderSid, "external reader");
        if (!externalReader.Value.StartsWith("S-1-15-3-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Immutable content external reader must be a Windows capability SID.");
        }

        if (string.IsNullOrWhiteSpace(HostReaderSid))
        {
            throw new InvalidDataException(
                "Immutable content HostReaderSid is required on Windows.");
        }

        var hostReader = ParseSid(HostReaderSid, "host reader");
        if (externalReader.Equals(hostReader))
        {
            throw new InvalidDataException(
                "Immutable content external and host readers must be different identities.");
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var current = identity.User
                      ?? throw new InvalidOperationException(
                          "Current Windows identity has no SID for immutable content protection.");
        if (!hostReader.Equals(current))
        {
            throw new InvalidDataException(
                "Immutable content HostReaderSid must be the current trusted control-plane identity.");
        }

        return new WindowsContentProtectionIdentities(externalReader, hostReader);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier ParseSid(string value, string role)
    {
        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Immutable content {role} SID is invalid.",
                exception);
        }

        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
        {
            throw new InvalidDataException(
                $"Immutable content {role} must be a dedicated non-administrative identity.");
        }

        return sid;
    }
}

internal sealed record WindowsContentProtectionIdentities(
    SecurityIdentifier ExternalReader,
    SecurityIdentifier HostReader)
{
    public IReadOnlyCollection<SecurityIdentifier> Readers => [ExternalReader, HostReader];
}

public interface IImmutableContentProtector
{
    void ProtectCacheBoundary(
        string cacheRootDirectory,
        ImmutableContentProtectionPolicy policy);

    void VerifyCacheBoundary(
        string cacheRootDirectory,
        ImmutableContentProtectionPolicy policy);

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
    [SupportedOSPlatform("windows")]
    private static FileSystemRights ReaderMutationRights =>
        FileSystemRights.Write
        | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles;

    [SupportedOSPlatform("windows")]
    private static FileSystemRights ExternalReaderMutationRights =>
        ReaderMutationRights
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    [SupportedOSPlatform("windows")]
    private static FileSystemRights CacheBoundaryDeletionRights =>
        FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles;

    public void ProtectCacheBoundary(
        string cacheRootDirectory,
        ImmutableContentProtectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var cacheRoot = ResolveRoot(cacheRootDirectory);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identities = policy.ResolveWindowsIdentities();
        var security = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(cacheRoot));
        if (security.AreAccessRulesProtected)
        {
            VerifyWindowsCacheBoundaryProtection(security, identities);
            return;
        }

        if (RequiresHostOwnerCanonicalization(
                security,
                identities.HostReader,
                "cache boundary"))
        {
            security.SetOwner(identities.HostReader);
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(cacheRoot), security);
            RequireHostOwner(
                FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(cacheRoot)),
                identities.HostReader,
                "cache boundary");
        }

        ApplyWindowsCacheBoundaryProtection(cacheRoot, identities);
        VerifyCacheBoundary(cacheRoot, policy);
    }

    public void VerifyCacheBoundary(
        string cacheRootDirectory,
        ImmutableContentProtectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var cacheRoot = ResolveRoot(cacheRootDirectory);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identities = policy.ResolveWindowsIdentities();
        VerifyWindowsCacheBoundaryProtection(
            FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(cacheRoot)),
            identities);
    }

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
            ProtectCacheBoundary(
                Path.GetDirectoryName(root)
                ?? throw new InvalidDataException("Immutable content root has no cache boundary."),
                policy);
            ProtectWindows(root, inventory, policy.ResolveWindowsIdentities());
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
            VerifyWindowsProtection(root, inventory, policy.ResolveWindowsIdentities());
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
    private static void ApplyWindowsCacheBoundaryProtection(
        string cacheRoot,
        WindowsContentProtectionIdentities identities)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            identities.ExternalReader,
            ExternalReaderMutationRights,
            AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(
            identities.ExternalReader,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            identities.HostReader,
            CacheBoundaryDeletionRights,
            AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(
            identities.HostReader,
            FileSystemRights.ReadAndExecute | FileSystemRights.CreateDirectories,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            identities.HostReader,
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly,
            AccessControlType.Allow));

        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(cacheRoot), security);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsCacheBoundaryProtection(
        DirectorySecurity security,
        WindowsContentProtectionIdentities identities)
    {
        if (!security.AreAccessRulesProtected)
        {
            throw new InvalidDataException(
                "Immutable content cache boundary ACL inheritance must be disabled.");
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (!HasHostOwner(security, identities.HostReader)
            || rules.Length != 7
            || rules.Any(rule => rule.IsInherited)
            || !HasCacheBoundaryAllowRule(rules, system, FileSystemRights.FullControl, inheritedByChildren: true)
            || !HasCacheBoundaryAllowRule(
                rules,
                administrators,
                FileSystemRights.FullControl,
                inheritedByChildren: true)
            || !HasCacheBoundaryAllowRule(
                rules,
                identities.ExternalReader,
                FileSystemRights.ReadAndExecute,
                inheritedByChildren: false)
            || !HasCacheBoundaryAllowRule(
                rules,
                identities.HostReader,
                FileSystemRights.ReadAndExecute | FileSystemRights.CreateDirectories,
                inheritedByChildren: false)
            || !HasCacheWriterInheritanceRule(rules, identities.HostReader)
            || !HasDeniedCacheBoundaryMutationRights(
                rules,
                identities.ExternalReader,
                ExternalReaderMutationRights)
            || !HasDeniedCacheBoundaryMutationRights(
                rules,
                identities.HostReader,
                CacheBoundaryDeletionRights))
        {
            throw new InvalidDataException(
                "Immutable content cache boundary owner and ACL do not match the trusted host "
                + "and external capability policy.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasCacheWriterInheritanceRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier cacheWriter) => HasCacheBoundaryAllowRule(
        rules,
        cacheWriter,
        FileSystemRights.Modify,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags.InheritOnly);

    [SupportedOSPlatform("windows")]
    private static bool HasCacheBoundaryAllowRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        FileSystemRights rights,
        bool inheritedByChildren) => HasCacheBoundaryAllowRule(
        rules,
        identity,
        rights,
        inheritedByChildren
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None,
        PropagationFlags.None);

    [SupportedOSPlatform("windows")]
    private static bool HasCacheBoundaryAllowRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        FileSystemRights rights,
        InheritanceFlags inheritanceFlags,
        PropagationFlags propagationFlags) => rules.Any(rule =>
        identity.Equals(rule.IdentityReference)
        && rule.AccessControlType == AccessControlType.Allow
        && rule.FileSystemRights == (rights | FileSystemRights.Synchronize)
        && rule.InheritanceFlags == inheritanceFlags
        && rule.PropagationFlags == propagationFlags);

    [SupportedOSPlatform("windows")]
    private static bool HasDeniedCacheBoundaryMutationRights(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        FileSystemRights rights) => rules.Any(rule =>
        identity.Equals(rule.IdentityReference)
        && rule.AccessControlType == AccessControlType.Deny
        && rule.FileSystemRights == rights
        && rule.InheritanceFlags == InheritanceFlags.None
        && rule.PropagationFlags == PropagationFlags.None);

    [SupportedOSPlatform("windows")]
    private static void ProtectWindows(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory,
        WindowsContentProtectionIdentities identities)
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
            ApplyFileProtection(file, identities);
        }

        foreach (var directory in directories)
        {
            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);
            ApplyDirectoryProtection(directory, identities);
        }

        File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.ReadOnly);
        ApplyDirectoryProtection(root, identities);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsProtection(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory,
        WindowsContentProtectionIdentities identities)
    {
        foreach (var file in inventory)
        {
            var path = ResolveFile(root, file.RelativePath);
            VerifyFileSystemSecurity(
                FileSystemAclExtensions.GetAccessControl(new FileInfo(path)),
                identities,
                InheritanceFlags.None);
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
                identities,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
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
        WindowsContentProtectionIdentities identities)
    {
        var fileInfo = new FileInfo(path);
        var existingSecurity = FileSystemAclExtensions.GetAccessControl(fileInfo);
        if (RequiresHostOwnerCanonicalization(
                existingSecurity,
                identities.HostReader,
                "file"))
        {
            existingSecurity.SetOwner(identities.HostReader);
            FileSystemAclExtensions.SetAccessControl(fileInfo, existingSecurity);
            RequireHostOwner(
                FileSystemAclExtensions.GetAccessControl(fileInfo),
                identities.HostReader,
                "file");
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRules(security, identities, InheritanceFlags.None);
        FileSystemAclExtensions.SetAccessControl(fileInfo, security);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyDirectoryProtection(
        string path,
        WindowsContentProtectionIdentities identities)
    {
        var directoryInfo = new DirectoryInfo(path);
        var existingSecurity = FileSystemAclExtensions.GetAccessControl(directoryInfo);
        if (RequiresHostOwnerCanonicalization(
                existingSecurity,
                identities.HostReader,
                "directory"))
        {
            existingSecurity.SetOwner(identities.HostReader);
            FileSystemAclExtensions.SetAccessControl(directoryInfo, existingSecurity);
            RequireHostOwner(
                FileSystemAclExtensions.GetAccessControl(directoryInfo),
                identities.HostReader,
                "directory");
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRules(
            security,
            identities,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        FileSystemAclExtensions.SetAccessControl(directoryInfo, security);
    }

    [SupportedOSPlatform("windows")]
    private static void AddRules(
        FileSystemSecurity security,
        WindowsContentProtectionIdentities identities,
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
        AddReaderRules(
            security,
            identities.ExternalReader,
            ExternalReaderMutationRights,
            inheritanceFlags);
        AddReaderRules(
            security,
            identities.HostReader,
            ReaderMutationRights,
            inheritanceFlags);
    }

    [SupportedOSPlatform("windows")]
    private static void AddReaderRules(
        FileSystemSecurity security,
        SecurityIdentifier reader,
        FileSystemRights deniedRights,
        InheritanceFlags inheritanceFlags)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            reader,
            deniedRights,
            inheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(
            reader,
            FileSystemRights.ReadAndExecute,
            inheritanceFlags,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyFileSystemSecurity(
        FileSystemSecurity security,
        WindowsContentProtectionIdentities identities,
        InheritanceFlags inheritanceFlags)
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
        if (!HasHostOwner(security, identities.HostReader)
            || rules.Length != 6
            || rules.Any(rule => rule.IsInherited)
            || !HasAllowRule(
                rules,
                system,
                FileSystemRights.FullControl,
                inheritanceFlags)
            || !HasAllowRule(
                rules,
                administrators,
                FileSystemRights.FullControl,
                inheritanceFlags)
            || !HasReaderRules(
                rules,
                identities.ExternalReader,
                ExternalReaderMutationRights,
                inheritanceFlags)
            || !HasReaderRules(
                rules,
                identities.HostReader,
                ReaderMutationRights,
                inheritanceFlags))
        {
            throw new InvalidDataException(
                "Immutable content owner and ACL must match the trusted host and external "
                + "capability policy exactly.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool RequiresHostOwnerCanonicalization(
        FileSystemSecurity security,
        SecurityIdentifier hostReader,
        string boundary)
    {
        var actualOwner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                          ?? throw new InvalidDataException(
                              $"Immutable content {boundary} owner SID is unavailable.");
        if (hostReader.Equals(actualOwner))
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var tokenDefaultOwner = identity.Owner
                                ?? identity.User
                                ?? throw new InvalidOperationException(
                                    "Current Windows identity has no default owner SID.");
        if (!tokenDefaultOwner.Equals(actualOwner))
        {
            throw new InvalidDataException(
                $"Immutable content {boundary} must be owned by the trusted HostReaderSid "
                + "or the current trusted token's default owner before protection.");
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireHostOwner(
        FileSystemSecurity security,
        SecurityIdentifier hostReader,
        string boundary)
    {
        if (!HasHostOwner(security, hostReader))
        {
            throw new InvalidDataException(
                $"Immutable content {boundary} must be owned by the trusted HostReaderSid.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasHostOwner(
        FileSystemSecurity security,
        SecurityIdentifier hostReader) => hostReader.Equals(
        security.GetOwner(typeof(SecurityIdentifier)));

    [SupportedOSPlatform("windows")]
    private static bool HasReaderRules(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier reader,
        FileSystemRights deniedRights,
        InheritanceFlags inheritanceFlags) => HasAllowRule(
        rules,
        reader,
        FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
        inheritanceFlags)
        && HasDeniedMutationRights(rules, reader, deniedRights, inheritanceFlags);

    [SupportedOSPlatform("windows")]
    private static bool HasAllowRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        FileSystemRights rights,
        InheritanceFlags inheritanceFlags) => rules.Any(rule =>
        identity.Equals(rule.IdentityReference)
        && rule.AccessControlType == AccessControlType.Allow
        && rule.FileSystemRights == rights
        && rule.InheritanceFlags == inheritanceFlags
        && rule.PropagationFlags == PropagationFlags.None);

    [SupportedOSPlatform("windows")]
    private static bool HasDeniedMutationRights(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier reader,
        FileSystemRights deniedRights,
        InheritanceFlags inheritanceFlags) => rules.Any(rule =>
        reader.Equals(rule.IdentityReference)
        && rule.AccessControlType == AccessControlType.Deny
        && rule.FileSystemRights == deniedRights
        && rule.InheritanceFlags == inheritanceFlags
        && rule.PropagationFlags == PropagationFlags.None);

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
        var rootSecurity = new DirectorySecurity();
        rootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        rootSecurity.AddAccessRule(new FileSystemAccessRule(
            current,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), rootSecurity);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(file), security);
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path.Length))
        {
            var security = new DirectorySecurity();
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
