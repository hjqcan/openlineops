using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.ContentProtection;

public sealed record ImmutableContentFile(
    string RelativePath,
    long SizeBytes,
    string Sha256);

public sealed record ImmutableContentProtectionPolicy(
    string ReaderSid,
    string? StationServiceSid = null)
{
    [SupportedOSPlatform("windows")]
    internal WindowsContentProtectionIdentities ResolveWindowsIdentities()
    {
        WindowsContentProtectionIdentities identities = ResolveConfiguredWindowsIdentities();
        _ = WindowsStationServiceIdentityReader.ReadRequired(
            identities.StationService.Value);
        return identities;
    }

    [SupportedOSPlatform("windows")]
    internal WindowsContentProtectionIdentities ResolveConfiguredWindowsIdentities()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows content protection identities require Windows.");
        }

        SecurityIdentifier externalReader = ParseSid(ReaderSid, "external reader");
        if (!externalReader.Value.StartsWith("S-1-15-3-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Immutable content external reader must be a Windows capability SID.");
        }

        if (string.IsNullOrWhiteSpace(StationServiceSid))
        {
            throw new InvalidDataException(
                "Immutable content StationServiceSid is required on Windows.");
        }

        var canonicalStationServiceSid =
            WindowsStationServiceIdentityReader.RequireCanonicalServiceSid(
                StationServiceSid,
                nameof(StationServiceSid));
        SecurityIdentifier stationServiceSid = ParseSid(
            canonicalStationServiceSid,
            "Station service");
        if (externalReader.Equals(stationServiceSid))
        {
            throw new InvalidDataException(
                "Immutable content external reader and Station service must be different identities.");
        }

        return new WindowsContentProtectionIdentities(externalReader, stationServiceSid);
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
    SecurityIdentifier StationService)
{
    public IReadOnlyCollection<SecurityIdentifier> Readers => [ExternalReader, StationService];
}

public sealed record WindowsStationServiceIdentity(
    string HostAccountSid,
    string ServiceSid,
    bool ServiceLogonSidEnabled,
    bool TokenHasRestrictions,
    bool ServiceSidEnabled,
    bool ServiceSidOwnerEligible,
    bool ServiceSidRestricted);

public static class WindowsStationServiceIdentityReader
{
    public const string LocalServiceSid = "S-1-5-19";
    public const string ServiceLogonSid = "S-1-5-6";

    private const string ServiceSidPrefix = "S-1-5-80-";
    private const uint SeGroupEnabled = 0x00000004;
    private const uint SeGroupOwner = 0x00000008;
    private const uint SeGroupUseForDenyOnly = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;

    public static string RequireCanonicalServiceName(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        if (!IsCanonicalServiceName(value))
        {
            throw new InvalidDataException(
                $"{parameterName} must contain 1-80 ASCII letters, digits, periods, underscores, or hyphens.");
        }

        return value!;
    }

    public static bool IsCanonicalServiceName(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 80
        && value.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_'
            or '-');

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Windows defines canonical per-service SIDs with SHA-1 over the uppercase UTF-16 service name.")]
    public static string ServiceSidFromNameRequired(string serviceName)
    {
        var canonicalServiceName = RequireCanonicalServiceName(
            serviceName,
            nameof(serviceName));
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(
            canonicalServiceName.ToUpperInvariant()));
        var subAuthorities = new string[5];
        for (var index = 0; index < subAuthorities.Length; index++)
        {
            subAuthorities[index] = BinaryPrimitives
                .ReadUInt32LittleEndian(hash.AsSpan(index * sizeof(uint), sizeof(uint)))
                .ToString(CultureInfo.InvariantCulture);
        }

        return ServiceSidPrefix + string.Join('-', subAuthorities);
    }

    public static string RequireCanonicalServiceSid(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        if (!IsCanonicalServiceSid(value))
        {
            throw new InvalidDataException(
                $"{parameterName} must be an exact canonical Windows service SID with five decimal subauthorities.");
        }

        return value!;
    }

    public static bool IsCanonicalServiceSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(ServiceSidPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value.Split('-');
        if (parts.Length != 9
            || parts[0] != "S"
            || parts[1] != "1"
            || parts[2] != "5"
            || parts[3] != "80")
        {
            return false;
        }

        for (var index = 4; index < parts.Length; index++)
        {
            if (!uint.TryParse(
                    parts[index],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var subAuthority)
                || !string.Equals(
                    subAuthority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    parts[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    public static WindowsStationServiceIdentity ReadRequired(string requiredServiceSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Station restricted service identity validation requires Windows.");
        }

        var canonicalServiceSid = RequireCanonicalServiceSid(
            requiredServiceSid,
            nameof(requiredServiceSid));
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User?.Value
                      ?? throw new InvalidOperationException(
                          "Current Windows token has no user SID.");
        List<TokenSid> groups = ReadTokenSids(identity.AccessToken, TokenInformationClass.TokenGroups);
        List<TokenSid> restrictedSids = ReadTokenSids(
            identity.AccessToken,
            TokenInformationClass.TokenRestrictedSids);
        var serviceLogonSidEnabled = groups.Any(group =>
            IsEnabledGroup(group, ServiceLogonSid));
        var enabled = groups.Any(group => IsEnabledGroup(group, canonicalServiceSid));
        var ownerEligible = groups.Any(group =>
            string.Equals(group.Sid, canonicalServiceSid, StringComparison.Ordinal)
            && (group.Attributes & SeGroupOwner) != 0
            && (group.Attributes & SeGroupUseForDenyOnly) == 0);
        var restricted = restrictedSids.Any(group =>
            string.Equals(group.Sid, canonicalServiceSid, StringComparison.Ordinal));
        var tokenHasRestrictions = ReadTokenHasRestrictions(identity.AccessToken);
        var result = new WindowsStationServiceIdentity(
            userSid,
            canonicalServiceSid,
            serviceLogonSidEnabled,
            tokenHasRestrictions,
            enabled,
            ownerEligible,
            restricted);
        Validate(result);
        return result;
    }

    public static void Validate(WindowsStationServiceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var canonicalServiceSid = RequireCanonicalServiceSid(
            identity.ServiceSid,
            nameof(identity.ServiceSid));
        if (!string.Equals(
                identity.HostAccountSid,
                LocalServiceSid,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Station processes must run with NT AUTHORITY\\LocalService as the token user.");
        }

        if (!identity.ServiceLogonSidEnabled)
        {
            throw new InvalidOperationException(
                $"Station process token does not contain enabled Windows service-logon SID '{ServiceLogonSid}'.");
        }

        if (!identity.TokenHasRestrictions)
        {
            throw new InvalidOperationException(
                "Station process token is not a restricted Windows token.");
        }

        if (!identity.ServiceSidEnabled)
        {
            throw new InvalidOperationException(
                $"Station service SID '{canonicalServiceSid}' is not enabled in TokenGroups.");
        }

        if (!identity.ServiceSidOwnerEligible)
        {
            throw new InvalidOperationException(
                "Station processes require the exact service SID to be eligible as an object owner.");
        }

        if (!identity.ServiceSidRestricted)
        {
            throw new InvalidOperationException(
                $"Station service SID '{canonicalServiceSid}' is absent from TokenRestrictedSids.");
        }
    }

    private static bool IsEnabledGroup(TokenSid group, string requiredSid) =>
        string.Equals(group.Sid, requiredSid, StringComparison.Ordinal)
        && (group.Attributes & SeGroupEnabled) != 0
        && (group.Attributes & SeGroupUseForDenyOnly) == 0;

    [SupportedOSPlatform("windows")]
    internal static bool ReadTokenHasRestrictions(SafeAccessTokenHandle token)
    {
        const TokenInformationClass informationClass =
            TokenInformationClass.TokenHasRestrictions;
        _ = GetTokenInformation(
            token,
            informationClass,
            IntPtr.Zero,
            0,
            out var requiredBytes);
        var sizingError = Marshal.GetLastPInvokeError();
        if (requiredBytes == 0 || sizingError != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(
                sizingError,
                $"Could not size Windows {informationClass} data.");
        }

        if (requiredBytes is not (sizeof(byte) or sizeof(uint)))
        {
            throw new InvalidDataException(
                $"Windows {informationClass} requires an unsupported Boolean data length of {requiredBytes} bytes.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (requiredBytes == sizeof(byte))
            {
                Marshal.WriteByte(buffer, 0);
            }
            else
            {
                Marshal.WriteInt32(buffer, 0);
            }

            if (!GetTokenInformation(
                    token,
                    informationClass,
                    buffer,
                    requiredBytes,
                    out var returnedBytes))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Could not read Windows {informationClass} data.");
            }

            if (returnedBytes != requiredBytes)
            {
                throw new InvalidDataException(
                    $"Windows {informationClass} returned {returnedBytes} bytes after requiring {requiredBytes} bytes.");
            }

            return returnedBytes == sizeof(byte)
                ? Marshal.ReadByte(buffer) != 0
                : Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<TokenSid> ReadTokenSids(
        SafeAccessTokenHandle token,
        TokenInformationClass informationClass)
    {
        _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var requiredBytes);
        var error = Marshal.GetLastPInvokeError();
        if (requiredBytes == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, $"Could not size Windows {informationClass} data.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!GetTokenInformation(
                    token,
                    informationClass,
                    buffer,
                    requiredBytes,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Could not read Windows {informationClass} data.");
            }

            var count = Marshal.ReadInt32(buffer);
            var itemOffset = Marshal.OffsetOf<TokenGroups>(nameof(TokenGroups.Groups)).ToInt32();
            var itemSize = Marshal.SizeOf<SidAndAttributes>();
            var result = new List<TokenSid>(count);
            for (var index = 0; index < count; index++)
            {
                var itemAddress = IntPtr.Add(buffer, checked(itemOffset + index * itemSize));
                SidAndAttributes item = Marshal.PtrToStructure<SidAndAttributes>(itemAddress);
                result.Add(new TokenSid(
                    new SecurityIdentifier(item.Sid).Value,
                    item.Attributes));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    private enum TokenInformationClass
    {
        TokenGroups = 2,
        TokenRestrictedSids = 11,
        TokenHasRestrictions = 21
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroups
    {
        public uint GroupCount;
        public SidAndAttributes Groups;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    private sealed record TokenSid(string Sid, uint Attributes);
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

    ValueTask RemoveProtectedPackageInstallationAsync(
        string cacheRootDirectory,
        string contentSha256,
        string windowsServiceName,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default);
}

public sealed class ImmutableContentProtector : IImmutableContentProtector
{
    private const int BufferSize = 64 * 1024;
    private const int ErrorHandleEof = 38;
    private const int ErrorAccessDenied = 5;
    private const int MaximumStagingEntryCount = 20_000;
    private const long MaximumStagingBytes = 4L * 1024 * 1024 * 1024;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileDeleteChildAccess = 0x00000040;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private static readonly IntPtr InvalidHandleValue = new(-1);
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
    private static FileSystemRights OwnerControlRights =>
        FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    [SupportedOSPlatform("windows")]
    private static FileSystemRights CacheBoundaryDeletionRights =>
        FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles;

    public void ProvisionCacheNamespace(
        string cacheRootDirectory,
        string windowsServiceName,
        ImmutableContentProtectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var cacheRoot = ResolveProvisioningPath(cacheRootDirectory);
        var anchor = Directory.GetParent(cacheRoot)?.FullName
                     ?? throw new InvalidDataException(
                         "Immutable content cache root must have a dedicated namespace anchor.");
        var anchorParent = Directory.GetParent(anchor)?.FullName
                           ?? throw new InvalidDataException(
                               "Immutable content cache namespace anchor must have an existing parent.");
        _ = ResolveRoot(anchorParent);

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(anchor);
            Directory.CreateDirectory(cacheRoot);
            VerifyDedicatedAnchorContents(anchor, cacheRoot);
            return;
        }

        WindowsContentProtectionIdentities identities = policy.ResolveConfiguredWindowsIdentities();
        var canonicalServiceName = WindowsStationServiceIdentityReader.RequireCanonicalServiceName(
            windowsServiceName,
            nameof(windowsServiceName));
        if (!string.Equals(
                WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(canonicalServiceName),
                identities.StationService.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Immutable cache provisioning service name does not match StationServiceSid.");
        }

        RequireWindowsServiceStopped(canonicalServiceName, allowMissing: true);
        SecurityIdentifier provisioningAuthority = ResolveWindowsAdministrativeCleanupAuthority();
        using SafeFileHandle anchorParentHandle = OpenCanonicalDirectoryHandle(
            anchorParent,
            allowDeleteSharing: false);
        var existingCanonicalNonEmptyCache = false;
        if (Directory.Exists(anchor))
        {
            RejectReparsePoint(anchor);
            VerifyNoAlternateDataStreams(anchor, "cache namespace anchor");
            VerifyDedicatedAnchorContents(anchor, cacheRoot, allowMissingCacheRoot: true);
            if (Directory.Exists(cacheRoot))
            {
                RejectReparsePoint(cacheRoot);
                VerifyNoAlternateDataStreams(cacheRoot, "cache root");
                if (Directory.EnumerateFileSystemEntries(
                        cacheRoot,
                        "*",
                        SearchOption.TopDirectoryOnly).Any())
                {
                    try
                    {
                        VerifyCacheBoundary(cacheRoot, policy);
                        existingCanonicalNonEmptyCache = true;
                    }
                    catch (Exception exception) when (exception is InvalidDataException
                                                      or UnauthorizedAccessException
                                                      or Win32Exception)
                    {
                        throw new InvalidDataException(
                            "A non-empty immutable content cache has a noncanonical namespace; administrative recovery is required.",
                            exception);
                    }
                }
            }
        }

        Directory.CreateDirectory(anchor);
        Directory.CreateDirectory(cacheRoot);
        RejectReparsePoint(anchor);
        RejectReparsePoint(cacheRoot);
        VerifyNoAlternateDataStreams(anchor, "cache namespace anchor");
        VerifyNoAlternateDataStreams(cacheRoot, "cache root");
        VerifyDedicatedAnchorContents(anchor, cacheRoot);
        using SafeFileHandle anchorHandle = OpenCanonicalDirectoryHandle(
            anchor,
            allowDeleteSharing: false);
        using SafeFileHandle cacheRootHandle = OpenCanonicalDirectoryHandle(
            cacheRoot,
            allowDeleteSharing: false);
        var cacheIdentity = GetStableDirectoryIdentity(cacheRoot);
        using var transactionLock = new ImmutableContentCacheTransactionLock(
            cacheRoot,
            policy.StationServiceSid);
        ImmutableContentCacheTransactionLease lease = transactionLock
            .AcquireAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
        try
        {
            RequireWindowsServiceStopped(canonicalServiceName, allowMissing: true);
            if (!string.Equals(
                    cacheIdentity,
                    GetStableDirectoryIdentity(cacheRoot),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Immutable content cache identity changed during namespace provisioning.");
            }

            if (existingCanonicalNonEmptyCache)
            {
                VerifyCacheBoundary(cacheRoot, policy);
                return;
            }

            ApplyWindowsNamespaceAnchorProtection(
                anchor,
                identities,
                provisioningAuthority);
            ApplyWindowsCacheBoundaryProtection(
                cacheRoot,
                identities,
                provisioningAuthority);
            VerifyCacheBoundary(cacheRoot, policy);
            if (!string.Equals(
                    cacheIdentity,
                    GetStableDirectoryIdentity(cacheRoot),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Immutable content cache identity changed during namespace provisioning.");
            }
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

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

        _ = policy.ResolveWindowsIdentities();
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

        WindowsContentProtectionIdentities identities = policy.ResolveConfiguredWindowsIdentities();
        VerifyTrustedCacheNamespace(cacheRoot, identities);
        VerifyWindowsCacheBoundaryProtection(
            FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(cacheRoot)),
            identities);
        if (IsCurrentRestrictedStationService(identities.StationService))
        {
            VerifyCurrentServiceCannotReplaceCacheNamespace(cacheRoot);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyTrustedCacheNamespace(
        string cacheRoot,
        WindowsContentProtectionIdentities identities)
    {
        var anchor = Directory.GetParent(cacheRoot)?.FullName
                     ?? throw new InvalidDataException(
                         "Immutable content cache root must have a dedicated namespace anchor.");
        var anchorSecurity = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(anchor));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        SecurityIdentifier owner = anchorSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                                   ?? throw new InvalidDataException(
                                       "Immutable content cache namespace anchor owner is unavailable.");
        FileSystemAccessRule[] rules = [.. anchorSecurity
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];
        var inheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        if (!anchorSecurity.AreAccessRulesProtected
            || (!system.Equals(owner) && !administrators.Equals(owner))
            || rules.Length != 4
            || rules.Any(rule => rule.IsInherited)
            || !HasAllowRule(rules, system, FileSystemRights.FullControl, inheritanceFlags)
            || !HasAllowRule(rules, administrators, FileSystemRights.FullControl, inheritanceFlags)
            || !HasAllowRule(
                rules,
                identities.StationService,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.None)
            || !HasAllowRule(
                rules,
                identities.ExternalReader,
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
                InheritanceFlags.None))
        {
            throw new InvalidDataException(
                "Immutable content cache namespace anchor must be owned and controlled by "
                + "LocalSystem and Builtin Administrators, with exact read/traverse access for "
                + "the Station service and external content capability.");
        }

        foreach (var boundary in new[] { cacheRoot, anchor })
        {
            if ((File.GetAttributes(boundary) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Immutable content cache namespace cannot traverse a reparse point.");
            }

            VerifyNoAlternateDataStreams(
                boundary,
                string.Equals(boundary, cacheRoot, StringComparison.OrdinalIgnoreCase)
                    ? "cache root"
                    : "cache namespace anchor");
        }

        VerifyCanonicalDirectoryHandlePath(cacheRoot);
        VerifyDedicatedAnchorContents(anchor, cacheRoot);
    }

    private static void VerifyDedicatedAnchorContents(
        string anchor,
        string cacheRoot,
        bool allowMissingCacheRoot = false)
    {
        string[] entries = Directory
            .EnumerateFileSystemEntries(anchor, "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (entries.Any(entry => !string.Equals(
                Path.GetFullPath(entry),
                cacheRoot,
                comparison))
            || entries.Length > 1
            || (!allowMissingCacheRoot
                && (entries.Length != 1 || !Directory.Exists(cacheRoot))))
        {
            throw new InvalidDataException(
                "Immutable content cache namespace anchor must contain only its dedicated cache root.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyCanonicalDirectoryHandlePath(string directory)
    {
        using SafeFileHandle handle = OpenCanonicalDirectoryHandle(
            directory,
            allowDeleteSharing: true);
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenCanonicalDirectoryHandle(
        string directory,
        bool allowDeleteSharing)
    {
        SafeFileHandle handle = CreateFile(
            ToExtendedWindowsPath(directory),
            desiredAccess: 0,
            FileShareRead | FileShareWrite | (allowDeleteSharing ? FileShareDelete : 0),
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not open the immutable content cache namespace by handle.");
        }

        var finalPath = new char[32_768];
        var length = GetFinalPathNameByHandle(
            handle,
            finalPath,
            checked((uint)finalPath.Length),
            flags: 0);
        if (length == 0 || length >= finalPath.Length)
        {
            handle.Dispose();
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not resolve the immutable content cache namespace final path.");
        }

        var resolved = NormalizeFinalWindowsPath(new string(
            finalPath,
            startIndex: 0,
            checked((int)length)));
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(resolved, expected, StringComparison.OrdinalIgnoreCase))
        {
            handle.Dispose();
            throw new InvalidDataException(
                "Immutable content cache namespace resolves through a reparse point or path alias.");
        }

        return handle;
    }

    private static string NormalizeFinalWindowsPath(string value)
    {
        const string devicePrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        var path = value.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + value[uncPrefix.Length..]
            : value.StartsWith(devicePrefix, StringComparison.Ordinal)
                ? value[devicePrefix.Length..]
                : value;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    [SupportedOSPlatform("windows")]
    private static string ToExtendedWindowsPath(string value)
    {
        const string devicePrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        var path = Path.GetFullPath(value);
        if (path.StartsWith(devicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return uncPrefix + path[2..];
        }

        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == Path.VolumeSeparatorChar
            && path[2] == Path.DirectorySeparatorChar)
        {
            return devicePrefix + path;
        }

        throw new InvalidDataException(
            "Immutable content native Windows paths must be absolute drive or UNC paths.");
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentRestrictedStationService(SecurityIdentifier stationService)
    {
        try
        {
            _ = WindowsStationServiceIdentityReader.ReadRequired(stationService.Value);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyCurrentServiceCannotReplaceCacheNamespace(string cacheRoot)
    {
        var current = cacheRoot;
        while (true)
        {
            RequireDirectoryAccessDenied(current, DeleteAccess, "DELETE");
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent is null)
            {
                break;
            }

            RequireDirectoryAccessDenied(
                parent.FullName,
                FileDeleteChildAccess,
                "FILE_DELETE_CHILD");
            current = parent.FullName;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RequireDirectoryAccessDenied(
        string directory,
        uint desiredAccess,
        string accessName)
    {
        using SafeFileHandle handle = CreateFile(
            ToExtendedWindowsPath(directory),
            desiredAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            throw new InvalidDataException(
                $"Restricted Station service token unexpectedly has {accessName} on cache namespace '{directory}'.");
        }

        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorAccessDenied)
        {
            throw new Win32Exception(
                error,
                $"Could not prove {accessName} denial on cache namespace '{directory}'.");
        }
    }

    public async ValueTask ProtectAsync(
        string rootDirectory,
        IReadOnlyCollection<ImmutableContentFile> files,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var root = ResolveRoot(rootDirectory);
        IReadOnlyCollection<ImmutableContentFile> inventory = ValidateInventory(root, files);
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
        IReadOnlyCollection<ImmutableContentFile> inventory = ValidateInventory(root, files);
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
        IReadOnlyCollection<ImmutableContentFile> inventory = ValidateInventory(root, files);
        return VerifyInventoryCoreAsync(root, inventory, cancellationToken);
    }

    public async ValueTask RemoveProtectedPackageInstallationAsync(
        string cacheRootDirectory,
        string contentSha256,
        string windowsServiceName,
        ImmutableContentProtectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!IsSha256(contentSha256))
        {
            throw new InvalidDataException(
                "Protected Station package removal requires a lowercase SHA-256 content identity.");
        }

        var cacheRoot = ResolveRoot(cacheRootDirectory);
        var cacheIdentity = GetStableDirectoryIdentity(cacheRoot);
        string? canonicalServiceName = null;
        if (OperatingSystem.IsWindows())
        {
            _ = ResolveWindowsAdministrativeCleanupAuthority();
            canonicalServiceName = WindowsStationServiceIdentityReader.RequireCanonicalServiceName(
                windowsServiceName,
                nameof(windowsServiceName));
            WindowsContentProtectionIdentities identities = policy.ResolveConfiguredWindowsIdentities();
            var expectedServiceSid = WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                canonicalServiceName);
            if (!string.Equals(
                    expectedServiceSid,
                    identities.StationService.Value,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Protected Station package removal service name does not match StationServiceSid.");
            }

            RequireWindowsServiceStopped(canonicalServiceName, allowMissing: false);
        }

        VerifyCacheBoundary(cacheRoot, policy);
        using var transactionLock = new ImmutableContentCacheTransactionLock(
            cacheRoot,
            policy.StationServiceSid);
        await using ImmutableContentCacheTransactionLease lease =
            await transactionLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCacheBoundary(cacheRoot, policy);
        if (!string.Equals(
                cacheIdentity,
                GetStableDirectoryIdentity(cacheRoot),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Immutable content cache identity changed during package removal.");
        }

        if (OperatingSystem.IsWindows())
        {
            RequireWindowsServiceStopped(canonicalServiceName!, allowMissing: false);
        }

        string[] stagingDirectories = InspectUnprotectedPackageStaging(
            cacheRoot,
            contentSha256);
        var contentDirectory = Path.Combine(cacheRoot, contentSha256);
        var commitDirectory = Path.Combine(cacheRoot, $".{contentSha256}.installed");
        var contentExists = HasCanonicalDirectDirectoryEntry(cacheRoot, contentSha256);
        var commitExists = HasCanonicalDirectDirectoryEntry(
            cacheRoot,
            $".{contentSha256}.installed");
        if (commitExists && !contentExists)
        {
            throw new InvalidDataException(
                "Protected Station package is RecoveryRequired because its commit exists without content.");
        }

        if (!contentExists)
        {
            foreach (var stagingDirectory in stagingDirectories)
            {
                DeleteUnprotectedStagingDirectory(cacheRoot, stagingDirectory);
            }

            return;
        }

        WindowsContentProtectionIdentities? windowsIdentities = OperatingSystem.IsWindows()
            ? policy.ResolveConfiguredWindowsIdentities()
            : null;
        SecurityIdentifier? cleanupAuthority = OperatingSystem.IsWindows()
            ? ResolveWindowsAdministrativeCleanupAuthority()
            : null;
        if (commitExists)
        {
            byte[] commitBytes = Encoding.ASCII.GetBytes(contentSha256 + "\n");
            ImmutableContentFile[] commitInventory =
            [
                new(
                    "content.sha256",
                    commitBytes.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(commitBytes)))
            ];
            if (OperatingSystem.IsWindows())
            {
                await VerifyInventoryAsync(
                        commitDirectory,
                        commitInventory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await VerifyAsync(
                        commitDirectory,
                        commitInventory,
                        policy,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsCleanupProvenance(
                contentDirectory,
                windowsIdentities!,
                cleanupAuthority!);
            if (commitExists)
            {
                VerifyWindowsCleanupProvenance(
                    commitDirectory,
                    windowsIdentities!,
                    cleanupAuthority!);
            }
        }

        foreach (var stagingDirectory in stagingDirectories)
        {
            DeleteUnprotectedStagingDirectory(cacheRoot, stagingDirectory);
        }

        if (commitExists)
        {
            DeleteProtectedEntry(
                cacheRoot,
                commitDirectory,
                windowsIdentities,
                cleanupAuthority);
        }

        DeleteProtectedEntry(
            cacheRoot,
            contentDirectory,
            windowsIdentities,
            cleanupAuthority);
    }

    private static string[] InspectUnprotectedPackageStaging(
        string cacheRoot,
        string contentSha256)
    {
        const int maximumStagingDirectories = 64;
        string[] entries = [.. Directory
            .EnumerateFileSystemEntries(
                cacheRoot,
                $".{contentSha256}.*",
                SearchOption.TopDirectoryOnly)
            .Take(maximumStagingDirectories + 2)];
        var stagingCount = 0;
        foreach (var entry in entries)
        {
            var leaf = Path.GetFileName(entry);
            if (string.Equals(
                    leaf,
                    $".{contentSha256}.installed",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsStagingDirectoryName(leaf))
            {
                throw new InvalidDataException(
                    $"Protected Station package is RecoveryRequired because cache entry '{leaf}' is not canonical.");
            }

            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Protected Station package is RecoveryRequired because staging '{leaf}' is not a regular directory.");
            }

            stagingCount++;
            if (stagingCount > maximumStagingDirectories)
            {
                throw new InvalidDataException(
                    "Protected Station package is RecoveryRequired because staging exceeds its bounded count.");
            }

        }

        return [.. entries.Where(entry => !string.Equals(
            Path.GetFileName(entry),
            $".{contentSha256}.installed",
            StringComparison.Ordinal))];
    }

    private static bool HasCanonicalDirectDirectoryEntry(
        string cacheRoot,
        string expectedLeaf)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        FileSystemInfo[] matches = [.. new DirectoryInfo(cacheRoot)
            .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
            .Where(entry => string.Equals(entry.Name, expectedLeaf, comparison))];
        if (matches.Length == 0)
        {
            return false;
        }

        FileSystemInfo entry = matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                "Protected Station package is RecoveryRequired because a cache identity is duplicated.");
        FileAttributes attributes = entry.Attributes;
        if (!string.Equals(entry.Name, expectedLeaf, StringComparison.Ordinal)
            || (attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Protected Station package is RecoveryRequired because cache entry '{entry.Name}' has a noncanonical type or name.");
        }

        return true;
    }

    private static void DeleteProtectedEntry(
        string cacheRoot,
        string entry,
        WindowsContentProtectionIdentities? windowsIdentities,
        SecurityIdentifier? cleanupAuthority)
    {
        _ = ResolveCacheEntry(cacheRoot, entry);
        RejectReparsePoint(entry);
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsCacheBoundaryProtection(
                FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(cacheRoot)),
                windowsIdentities!);
            VerifyWindowsCleanupProvenance(entry, windowsIdentities!, cleanupAuthority!);
            GrantCleanupAuthorityFullControl(
                entry,
                windowsIdentities!.StationService,
                cleanupAuthority!);
        }
        else
        {
            RestoreUnixWriteAccess(entry);
        }

        ClearReadOnlyAttributes(entry);
        Directory.Delete(entry, recursive: true);
    }

    public static void DeleteUnprotectedStagingDirectory(
        string cacheRootDirectory,
        string stagingDirectory)
    {
        (var cacheRoot, var staging) = ResolveCacheEntry(
            cacheRootDirectory,
            stagingDirectory);
        var stagingLeaf = Path.GetFileName(staging);
        if (!IsStagingDirectoryName(stagingLeaf))
        {
            throw new InvalidDataException(
                "Unprotected cleanup is restricted to an installing or committing staging directory.");
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsHandleBoundTreeDeletion.DeleteDirectChildTree(
                cacheRoot,
                stagingLeaf,
                MaximumStagingEntryCount,
                MaximumStagingBytes);
            return;
        }

        if (!HasCanonicalDirectDirectoryEntry(cacheRoot, stagingLeaf))
        {
            return;
        }

        RejectReparsePoint(staging);
        var directories = new List<string>();
        var files = new List<string>();
        var pending = new Stack<string>();
        var entryCount = 1;
        long totalBytes = 0;
        pending.Push(staging);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Unprotected staging cleanup cannot traverse a reparse point.");
            }

            if (OperatingSystem.IsWindows())
            {
                VerifyNoAlternateDataStreams(
                    directory,
                    PortableRelativePath(cacheRoot, directory));
            }

            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                entryCount++;
                if (entryCount > MaximumStagingEntryCount)
                {
                    throw new InvalidDataException(
                        "Unprotected staging cleanup exceeds its bounded entry count.");
                }

                var entryAttributes = File.GetAttributes(entry);
                if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Unprotected staging cleanup cannot traverse a reparse point.");
                }

                if ((entryAttributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if (OperatingSystem.IsWindows())
                {
                    using SafeFileHandle fileHandle = File.OpenHandle(
                        entry,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        FileOptions.SequentialScan);
                    WindowsFileIdentity identity = ReadRequiredSingleLinkFileIdentity(
                        fileHandle,
                        PortableRelativePath(cacheRoot, entry));
                    totalBytes = AddBoundedStagingBytes(totalBytes, identity.SizeBytes);
                    VerifyNoAlternateDataStreams(
                        entry,
                        PortableRelativePath(cacheRoot, entry));
                }
                else
                {
                    totalBytes = AddBoundedStagingBytes(
                        totalBytes,
                        new FileInfo(entry).Length);
                }

                files.Add(entry);
            }
        }

        foreach (var file in files)
        {
            File.Delete(file);
        }

        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            Directory.Delete(directory);
        }
    }

    private static long AddBoundedStagingBytes(long current, long additional)
    {
        long total;
        try
        {
            total = checked(current + additional);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Unprotected staging cleanup exceeds its bounded byte size.",
                exception);
        }

        if (additional < 0 || total > MaximumStagingBytes)
        {
            throw new InvalidDataException(
                "Unprotected staging cleanup exceeds its bounded byte size.");
        }

        return total;
    }

    public static string GetStableDirectoryIdentity(string directory)
    {
        var root = ResolveRoot(directory);
        if (!OperatingSystem.IsWindows())
        {
            return root;
        }

        VerifyCanonicalDirectoryHandlePath(root);
        using SafeFileHandle handle = CreateFile(
            ToExtendedWindowsPath(root),
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not open immutable content directory identity by handle.");
        }

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information)
            || (((FileAttributes)information.FileAttributes) & FileAttributes.Directory) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                "Could not inspect immutable content directory identity.");
        }

        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return information.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture)
               + "-"
               + fileIndex.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static (string CacheRoot, string Content) ResolveCacheEntry(
        string cacheRootDirectory,
        string contentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDirectory);
        var cacheRoot = ResolveRoot(cacheRootDirectory);
        var content = Path.GetFullPath(contentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!Path.IsPathFullyQualified(contentDirectory)
            || !string.Equals(
                content,
                contentDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                comparison)
            || !content.StartsWith(cacheRoot + Path.DirectorySeparatorChar, comparison)
            || !string.Equals(Path.GetDirectoryName(content), cacheRoot, comparison))
        {
            throw new InvalidDataException(
                "Protected content cleanup is restricted to one direct cache entry.");
        }

        var leaf = Path.GetFileName(content);
        if (!IsSha256(leaf)
            && !IsStagingDirectoryName(leaf)
            && !IsInstalledDirectoryName(leaf))
        {
            throw new InvalidDataException(
                "Protected content cleanup requires a content-addressed installation path.");
        }

        return (cacheRoot, content);
    }

    private static bool IsStagingDirectoryName(string leaf)
    {
        var segments = leaf.Split('.');
        return segments.Length == 4
               && segments[0].Length == 0
               && IsSha256(segments[1])
               && Guid.TryParseExact(segments[2], "N", out _)
               && (string.Equals(segments[3], "installing", StringComparison.Ordinal)
                   || string.Equals(segments[3], "committing", StringComparison.Ordinal));
    }

    private static bool IsInstalledDirectoryName(string leaf)
    {
        var segments = leaf.Split('.');
        return segments.Length == 3
               && segments[0].Length == 0
               && IsSha256(segments[1])
               && string.Equals(segments[2], "installed", StringComparison.Ordinal);
    }

    private static void RejectReparsePoint(string content)
    {
        if ((File.GetAttributes(content) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Protected content cleanup cannot target a reparse point.");
        }
    }

    private static void ClearReadOnlyAttributes(string content)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     content,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        File.SetAttributes(content, File.GetAttributes(content) & ~FileAttributes.ReadOnly);
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

        if (OperatingSystem.IsWindows())
        {
            RequireWindowsLocalNtfsPath(root);
        }

        return root;
    }

    private static string ResolveProvisioningPath(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var resolved = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!Path.IsPathFullyQualified(directory)
            || !string.Equals(
                resolved,
                directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                comparison)
            || string.Equals(
                resolved,
                Path.GetPathRoot(resolved)?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                comparison))
        {
            throw new InvalidDataException(
                "Immutable content cache provisioning requires a canonical absolute non-root directory.");
        }

        if (OperatingSystem.IsWindows())
        {
            RequireWindowsLocalNtfsPath(resolved);
        }

        return resolved;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsLocalNtfsPath(string path)
    {
        var volumeRoot = Path.GetPathRoot(path);
        if (volumeRoot is null
            || volumeRoot.Length != 3
            || volumeRoot[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z')
            || volumeRoot[1] != ':'
            || volumeRoot[2] != Path.DirectorySeparatorChar)
        {
            throw new InvalidDataException(
                "Immutable content must use a canonical local drive-letter path.");
        }

        var drive = new DriveInfo(volumeRoot);
        if (drive.DriveType != DriveType.Fixed
            || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Immutable content must use a local fixed NTFS volume.");
        }
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
        foreach (ImmutableContentFile file in files)
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
        if (OperatingSystem.IsWindows())
        {
            VerifyNoAlternateDataStreams(root, ".");
        }

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
            if (OperatingSystem.IsWindows())
            {
                VerifyNoAlternateDataStreams(path, relative);
            }

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

        foreach (ImmutableContentFile file in inventory)
        {
            var path = ResolveFile(root, file.RelativePath);
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            WindowsFileIdentity? windowsIdentity = OperatingSystem.IsWindows()
                ? ReadRequiredSingleLinkFileIdentity(stream.SafeFileHandle, file.RelativePath)
                : null;
            var actualSize = windowsIdentity?.SizeBytes ?? stream.Length;
            if (actualSize != file.SizeBytes)
            {
                throw new InvalidDataException(
                    $"Immutable content file '{file.RelativePath}' has an unexpected size.");
            }

            var hash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (OperatingSystem.IsWindows()
                && windowsIdentity is not null
                && windowsIdentity != ReadRequiredSingleLinkFileIdentity(
                    stream.SafeFileHandle,
                    file.RelativePath))
            {
                throw new InvalidDataException(
                    $"Immutable content file '{file.RelativePath}' changed identity while it was verified.");
            }

            if (!string.Equals(hash, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Immutable content file '{file.RelativePath}' failed SHA-256 verification.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsFileIdentity ReadRequiredSingleLinkFileIdentity(
        SafeFileHandle fileHandle,
        string displayPath)
    {
        if (!GetFileInformationByHandle(fileHandle, out ByHandleFileInformation information))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not inspect immutable content file '{displayPath}'.");
        }

        var attributes = (FileAttributes)information.FileAttributes;
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException(
                $"Immutable content file '{displayPath}' is not a regular file.");
        }

        if (information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                $"Immutable content file '{displayPath}' must have exactly one hard link.");
        }

        var size = ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;
        if (size > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Immutable content file '{displayPath}' is too large.");
        }

        return new WindowsFileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks,
            checked((long)size));
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyNoAlternateDataStreams(string path, string displayPath)
    {
        IntPtr searchHandle = FindFirstStream(
            ToExtendedWindowsPath(path),
            StreamInfoLevels.FindStreamInfoStandard,
            out Win32FindStreamData streamData,
            reserved: 0);
        if (searchHandle == InvalidHandleValue)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorHandleEof)
            {
                return;
            }

            throw new Win32Exception(
                error,
                $"Could not enumerate data streams for immutable content '{displayPath}'.");
        }

        try
        {
            while (true)
            {
                if (!string.Equals(streamData.StreamName, "::$DATA", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Immutable content '{displayPath}' contains a named alternate data stream.");
                }

                if (FindNextStream(searchHandle, out streamData))
                {
                    continue;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorHandleEof)
                {
                    throw new Win32Exception(
                        error,
                        $"Could not finish enumerating data streams for immutable content '{displayPath}'.");
                }

                return;
            }
        }
        finally
        {
            _ = FindClose(searchHandle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsNamespaceAnchorProtection(
        string anchor,
        WindowsContentProtectionIdentities identities,
        SecurityIdentifier owner)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var inheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
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
        security.AddAccessRule(new FileSystemAccessRule(
            identities.StationService,
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            identities.ExternalReader,
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(anchor), security);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsCacheBoundaryProtection(
        string cacheRoot,
        WindowsContentProtectionIdentities identities,
        SecurityIdentifier owner)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
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
            identities.StationService,
            CacheBoundaryDeletionRights,
            AccessControlType.Deny));
        security.AddAccessRule(new FileSystemAccessRule(
            identities.StationService,
            FileSystemRights.ReadAndExecute | FileSystemRights.CreateDirectories,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            identities.StationService,
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
        FileSystemAccessRule[] rules = [.. security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];
        if (!HasAdministrativeOwner(security)
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
                identities.StationService,
                FileSystemRights.ReadAndExecute | FileSystemRights.CreateDirectories,
                inheritedByChildren: false)
            || !HasCacheWriterInheritanceRule(rules, identities.StationService)
            || !HasDeniedCacheBoundaryMutationRights(
                rules,
                identities.ExternalReader,
                ExternalReaderMutationRights)
            || !HasDeniedCacheBoundaryMutationRights(
                rules,
                identities.StationService,
                CacheBoundaryDeletionRights))
        {
            throw new InvalidDataException(
                "Immutable content cache boundary owner and ACL do not match the Station service "
                + "and external capability policy.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasAdministrativeOwner(FileSystemSecurity security)
    {
        SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                                   ?? throw new InvalidDataException(
                                       "Immutable content owner SID is unavailable.");
        return owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
               || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
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
        string[] directories = AllInventoryDirectories(root, inventory);
        foreach (var file in files)
        {
            if (IsProtectedFileSystemObject(
                    FileSystemAclExtensions.GetAccessControl(new FileInfo(file)),
                    identities,
                    InheritanceFlags.None,
                    File.GetAttributes(file)))
            {
                continue;
            }

            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            ApplyFileProtection(file, identities);
        }

        foreach (var directory in directories)
        {
            if (IsProtectedFileSystemObject(
                    FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory)),
                    identities,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    File.GetAttributes(directory)))
            {
                continue;
            }

            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);
            ApplyDirectoryProtection(directory, identities);
        }

    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsProtection(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory,
        WindowsContentProtectionIdentities identities)
    {
        foreach (ImmutableContentFile file in inventory)
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

        foreach (var directory in AllInventoryDirectories(root, inventory))
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
        FileSecurity existingSecurity = FileSystemAclExtensions.GetAccessControl(fileInfo);
        RequireCanonicalPreSealSecurity(
            existingSecurity,
            identities,
            InheritanceFlags.None,
            "file");
        if (RequiresStationServiceOwnerCanonicalization(
                existingSecurity,
                identities.StationService,
                "file"))
        {
            existingSecurity.SetOwner(identities.StationService);
            FileSystemAclExtensions.SetAccessControl(fileInfo, existingSecurity);
            RequireStationServiceOwner(
                FileSystemAclExtensions.GetAccessControl(fileInfo),
                identities.StationService,
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
        DirectorySecurity existingSecurity = FileSystemAclExtensions.GetAccessControl(directoryInfo);
        RequireCanonicalPreSealSecurity(
            existingSecurity,
            identities,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            "directory");
        if (RequiresStationServiceOwnerCanonicalization(
                existingSecurity,
                identities.StationService,
                "directory"))
        {
            existingSecurity.SetOwner(identities.StationService);
            FileSystemAclExtensions.SetAccessControl(directoryInfo, existingSecurity);
            RequireStationServiceOwner(
                FileSystemAclExtensions.GetAccessControl(directoryInfo),
                identities.StationService,
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
            identities.StationService,
            ReaderMutationRights,
            inheritanceFlags);
        AddOwnerRightsDenyRule(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AddOwnerRightsDenyRule(FileSystemSecurity security) =>
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WinCreatorOwnerRightsSid, null),
            OwnerControlRights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));

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
        if (!HasExactFileSystemSecurity(security, identities, inheritanceFlags))
        {
            throw new InvalidDataException(
                "Immutable content owner and ACL must match the Station service and external "
                + "capability policy exactly.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasExactFileSystemSecurity(
        FileSystemSecurity security,
        WindowsContentProtectionIdentities identities,
        InheritanceFlags inheritanceFlags)
    {
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        FileSystemAccessRule[] rules = [.. security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];
        return HasStationServiceOwner(security, identities.StationService)
            && rules.Length == 7
            && rules.All(rule => !rule.IsInherited)
            && HasAllowRule(
                rules,
                system,
                FileSystemRights.FullControl,
                inheritanceFlags)
            && HasAllowRule(
                rules,
                administrators,
                FileSystemRights.FullControl,
                inheritanceFlags)
            && HasReaderRules(
                rules,
                identities.ExternalReader,
                ExternalReaderMutationRights,
                inheritanceFlags)
            && HasReaderRules(
                rules,
                identities.StationService,
                ReaderMutationRights,
                inheritanceFlags)
            && HasDeniedOwnerControlRule(rules);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsProtectedFileSystemObject(
        FileSystemSecurity security,
        WindowsContentProtectionIdentities identities,
        InheritanceFlags inheritanceFlags,
        FileAttributes attributes)
    {
        return HasExactFileSystemSecurity(security, identities, inheritanceFlags)
               && (attributes & FileAttributes.ReadOnly) != 0;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireCanonicalPreSealSecurity(
        FileSystemSecurity security,
        WindowsContentProtectionIdentities identities,
        InheritanceFlags inheritanceFlags,
        string boundary)
    {
        if (security.AreAccessRulesProtected)
        {
            throw new InvalidDataException(
                $"Immutable content {boundary} has a noncanonical protected ACL before sealing.");
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        FileSystemAccessRule[] rules = [.. security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];
        if (rules.Length != 3
            || rules.Any(rule => !rule.IsInherited || rule.AccessControlType != AccessControlType.Allow)
            || !HasInheritedPreSealRule(
                rules,
                system,
                FileSystemRights.FullControl,
                inheritanceFlags)
            || !HasInheritedPreSealRule(
                rules,
                administrators,
                FileSystemRights.FullControl,
                inheritanceFlags)
            || !HasInheritedPreSealRule(
                rules,
                identities.StationService,
                FileSystemRights.Modify,
                inheritanceFlags))
        {
            throw new InvalidDataException(
                $"Immutable content {boundary} ACL is not the exact cache-inherited pre-seal policy.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasInheritedPreSealRule(
        IEnumerable<FileSystemAccessRule> rules,
        SecurityIdentifier identity,
        FileSystemRights rights,
        InheritanceFlags inheritanceFlags) => rules.Any(rule =>
        identity.Equals(rule.IdentityReference)
        && rule.AccessControlType == AccessControlType.Allow
        && rule.FileSystemRights == (rights | FileSystemRights.Synchronize)
        && rule.IsInherited
        && rule.InheritanceFlags == inheritanceFlags
        && rule.PropagationFlags == PropagationFlags.None);

    [SupportedOSPlatform("windows")]
    private static bool RequiresStationServiceOwnerCanonicalization(
        FileSystemSecurity security,
        SecurityIdentifier stationService,
        string boundary)
    {
        SecurityIdentifier actualOwner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                          ?? throw new InvalidDataException(
                              $"Immutable content {boundary} owner SID is unavailable.");
        if (stationService.Equals(actualOwner))
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier tokenDefaultOwner = identity.Owner
                                ?? throw new InvalidOperationException(
                                    "Current Windows identity has no default owner SID.");
        if (!tokenDefaultOwner.Equals(actualOwner))
        {
            throw new InvalidDataException(
                $"Immutable content {boundary} must be owned by the configured StationServiceSid "
                + "or the current LocalService token's default owner before protection.");
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireStationServiceOwner(
        FileSystemSecurity security,
        SecurityIdentifier stationService,
        string boundary)
    {
        if (!HasStationServiceOwner(security, stationService))
        {
            throw new InvalidDataException(
                $"Immutable content {boundary} must be owned by the configured StationServiceSid.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasStationServiceOwner(
        FileSystemSecurity security,
        SecurityIdentifier stationService) => stationService.Equals(
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

    [SupportedOSPlatform("windows")]
    private static bool HasDeniedOwnerControlRule(
        IEnumerable<FileSystemAccessRule> rules)
    {
        var ownerRights = new SecurityIdentifier(
            WellKnownSidType.WinCreatorOwnerRightsSid,
            null);
        return rules.Any(rule =>
            ownerRights.Equals(rule.IdentityReference)
            && rule.AccessControlType == AccessControlType.Deny
            && rule.FileSystemRights == OwnerControlRights
            && rule.InheritanceFlags == InheritanceFlags.None
            && rule.PropagationFlags == PropagationFlags.None);
    }

    [UnsupportedOSPlatform("windows")]
    private static void ProtectUnix(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory)
    {
        foreach (ImmutableContentFile file in inventory)
        {
            var path = ResolveFile(root, file.RelativePath);
            UnixFileMode prior = File.GetUnixFileMode(path);
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

        foreach (var directory in AllInventoryDirectories(root, inventory))
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
        foreach (ImmutableContentFile file in inventory)
        {
            if ((File.GetUnixFileMode(ResolveFile(root, file.RelativePath)) & writeBits) != 0)
            {
                throw new InvalidDataException(
                    $"Immutable content file '{file.RelativePath}' is writable.");
            }
        }

        foreach (var directory in AllInventoryDirectories(root, inventory))
        {
            if ((File.GetUnixFileMode(directory) & writeBits) != 0)
            {
                throw new InvalidDataException(
                    $"Immutable content directory '{directory}' is writable.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier ResolveWindowsAdministrativeCleanupAuthority()
    {
        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        SecurityIdentifier user = identity.User
                                  ?? throw new InvalidOperationException(
                                      "Current Windows cleanup token has no user SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (system.Equals(user))
        {
            return system;
        }

        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
            || !ReadCurrentTokenElevation(identity.AccessToken))
        {
            throw new UnauthorizedAccessException(
                "Immutable cache administration requires LocalSystem or an elevated token "
                + "with the enabled Builtin Administrators SID.");
        }

        return administrators;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireWindowsServiceStopped(
        string serviceName,
        bool allowMissing)
    {
        const uint ScManagerConnect = 0x0001;
        const uint ServiceQueryStatus = 0x0004;
        const uint ServiceStopped = 0x00000001;
        using SafeServiceHandle serviceManager = OpenServiceControlManager(
            null,
            null,
            ScManagerConnect);
        if (serviceManager.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not open Windows Service Control Manager for immutable cache administration.");
        }

        using SafeServiceHandle service = OpenWindowsService(
            serviceManager,
            serviceName,
            ServiceQueryStatus);
        if (service.IsInvalid)
        {
            const int ErrorServiceDoesNotExist = 1060;
            var error = Marshal.GetLastPInvokeError();
            if (allowMissing && error == ErrorServiceDoesNotExist)
            {
                return;
            }

            throw new Win32Exception(
                error,
                $"Could not open Station service '{serviceName}' for immutable cache administration.");
        }

        var bufferSize = Marshal.SizeOf<ServiceStatusProcess>();
        if (!QueryWindowsServiceStatus(
                service,
                ServiceStatusInfo.Process,
                out ServiceStatusProcess status,
                bufferSize,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not query Station service '{serviceName}' for immutable cache administration.");
        }

        if (status.CurrentState != ServiceStopped || status.ProcessId != 0)
        {
            throw new InvalidOperationException(
                $"Station service '{serviceName}' must be fully stopped before immutable cache administration.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ReadCurrentTokenElevation(SafeAccessTokenHandle accessToken)
    {
        if (!GetCleanupTokenInformation(
                accessToken,
                CleanupTokenInformationClass.TokenElevation,
                out var elevated,
                sizeof(int),
                out var returnedLength))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not inspect the Windows cleanup token elevation.");
        }

        if (returnedLength != sizeof(int))
        {
            throw new InvalidDataException(
                "Windows cleanup token elevation returned an invalid data length.");
        }

        return elevated != 0;
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsCleanupProvenance(
        string root,
        WindowsContentProtectionIdentities identities,
        SecurityIdentifier cleanupAuthority)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Protected Station content cleanup cannot traverse a reparse point.");
            }

            VerifyNoAlternateDataStreams(
                directory,
                PortableRelativePath(root, directory));

            VerifyWindowsCleanupObjectSecurity(
                FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory)),
                identities,
                cleanupAuthority,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                File.GetAttributes(directory),
                "directory");

            foreach (var file in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Protected Station content cleanup cannot traverse a reparse point.");
                }

                using (SafeFileHandle fileHandle = File.OpenHandle(
                           file,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           FileOptions.SequentialScan))
                {
                    _ = ReadRequiredSingleLinkFileIdentity(
                        fileHandle,
                        PortableRelativePath(root, file));
                }
                VerifyNoAlternateDataStreams(
                    file,
                    PortableRelativePath(root, file));

                VerifyWindowsCleanupObjectSecurity(
                    FileSystemAclExtensions.GetAccessControl(new FileInfo(file)),
                    identities,
                    cleanupAuthority,
                    InheritanceFlags.None,
                    File.GetAttributes(file),
                    "file");
            }

            foreach (var child in Directory.EnumerateDirectories(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                pending.Push(child);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsCleanupObjectSecurity(
        FileSystemSecurity security,
        WindowsContentProtectionIdentities identities,
        SecurityIdentifier cleanupAuthority,
        InheritanceFlags inheritanceFlags,
        FileAttributes attributes,
        string boundary)
    {
        var sealedState = HasExactFileSystemSecurity(
            security,
            identities,
            inheritanceFlags);
        var cleanupState = HasExactCleanupSecurity(
            security,
            identities.StationService,
            cleanupAuthority,
            inheritanceFlags);
        var preSealState = HasExactPreSealSecurityForCleanup(
            security,
            identities,
            inheritanceFlags);
        if (!sealedState && !cleanupState && !preSealState)
        {
            throw new InvalidDataException(
                $"Protected Station content cleanup {boundary} ACL is not a sealed, pre-seal, or exact cleanup transition.");
        }

        if (sealedState && (attributes & FileAttributes.ReadOnly) == 0)
        {
            throw new InvalidDataException(
                $"Protected Station content cleanup requires sealed {boundary} objects to remain read-only.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasExactPreSealSecurityForCleanup(
        FileSystemSecurity security,
        WindowsContentProtectionIdentities identities,
        InheritanceFlags inheritanceFlags)
    {
        if (security.AreAccessRulesProtected)
        {
            return false;
        }

        SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                                   ?? throw new InvalidDataException(
                                       "Immutable pre-seal content owner SID is unavailable.");
        var localService = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        if (!identities.StationService.Equals(owner) && !localService.Equals(owner))
        {
            return false;
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        FileSystemAccessRule[] rules = [.. security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];
        return rules.Length == 3
               && rules.All(rule => rule.IsInherited && rule.AccessControlType == AccessControlType.Allow)
               && HasInheritedPreSealRule(
                   rules,
                   system,
                   FileSystemRights.FullControl,
                   inheritanceFlags)
               && HasInheritedPreSealRule(
                   rules,
                   administrators,
                   FileSystemRights.FullControl,
                   inheritanceFlags)
               && HasInheritedPreSealRule(
                   rules,
                   identities.StationService,
                   FileSystemRights.Modify,
                   inheritanceFlags);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasExactCleanupSecurity(
        FileSystemSecurity security,
        SecurityIdentifier stationService,
        SecurityIdentifier cleanupAuthority,
        InheritanceFlags inheritanceFlags)
    {
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        SecurityIdentifier owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                                   ?? throw new InvalidDataException(
                                       "Immutable cleanup content owner SID is unavailable.");
        var localService = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        if (!stationService.Equals(owner) && !localService.Equals(owner))
        {
            return false;
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        SecurityIdentifier[] authorities = [.. new[]
            {
                system,
                administrators,
                cleanupAuthority
            }.DistinctBy(identity => identity.Value, StringComparer.Ordinal)];
        FileSystemAccessRule[] rules = [.. security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()];
        return rules.Length == authorities.Length + 1
               && rules.All(rule => !rule.IsInherited)
               && authorities.All(authority => HasAllowRule(
                   rules,
                   authority,
                   FileSystemRights.FullControl,
                   inheritanceFlags))
               && HasDeniedOwnerControlRule(rules);
    }

    [SupportedOSPlatform("windows")]
    private static void GrantCleanupAuthorityFullControl(
        string root,
        SecurityIdentifier stationService,
        SecurityIdentifier cleanupAuthority)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using SafeFileHandle fileHandle = File.OpenHandle(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);
            WindowsFileIdentity identityBefore = ReadRequiredSingleLinkFileIdentity(
                fileHandle,
                PortableRelativePath(root, file));
            VerifyNoAlternateDataStreams(file, PortableRelativePath(root, file));
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddCleanupFullControlRules(
                fileSecurity,
                cleanupAuthority,
                InheritanceFlags.None);
            FileSystemAclExtensions.SetAccessControl(new FileInfo(file), fileSecurity);
            RequireExactCleanupSecurity(
                FileSystemAclExtensions.GetAccessControl(new FileInfo(file)),
                stationService,
                cleanupAuthority,
                InheritanceFlags.None,
                "file");
            if (identityBefore != ReadRequiredSingleLinkFileIdentity(
                    fileHandle,
                    PortableRelativePath(root, file)))
            {
                throw new InvalidDataException(
                    "Protected Station cleanup target changed identity during ACL recovery.");
            }
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Protected Station content cleanup cannot traverse a reparse point.");
            }
            VerifyNoAlternateDataStreams(
                directory,
                PortableRelativePath(root, directory));

            var directorySecurity = new DirectorySecurity();
            directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddCleanupFullControlRules(
                directorySecurity,
                cleanupAuthority,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            FileSystemAclExtensions.SetAccessControl(
                new DirectoryInfo(directory),
                directorySecurity);
            RequireExactCleanupSecurity(
                FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(directory)),
                stationService,
                cleanupAuthority,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                "directory");
        }

        var rootSecurity = new DirectorySecurity();
        rootSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddCleanupFullControlRules(
            rootSecurity,
            cleanupAuthority,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
        FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), rootSecurity);
        RequireExactCleanupSecurity(
            FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(root)),
            stationService,
            cleanupAuthority,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            "root directory");
    }

    [SupportedOSPlatform("windows")]
    private static void RequireExactCleanupSecurity(
        FileSystemSecurity security,
        SecurityIdentifier stationService,
        SecurityIdentifier cleanupAuthority,
        InheritanceFlags inheritanceFlags,
        string boundary)
    {
        if (!HasExactCleanupSecurity(
                security,
                stationService,
                cleanupAuthority,
                inheritanceFlags))
        {
            throw new InvalidDataException(
                $"Protected Station cleanup {boundary} ACL transition did not become exact.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddCleanupFullControlRules(
        FileSystemSecurity security,
        SecurityIdentifier cleanupAuthority,
        InheritanceFlags inheritanceFlags)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        foreach (SecurityIdentifier authority in new[]
                 {
                     system,
                     administrators,
                     cleanupAuthority
                 }.DistinctBy(identity => identity.Value, StringComparer.Ordinal))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                authority,
                FileSystemRights.FullControl,
                inheritanceFlags,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        AddOwnerRightsDenyRule(security);
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
        StringComparison comparison = OperatingSystem.IsWindows()
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

    private static string[] AllInventoryDirectories(
        string root,
        IReadOnlyCollection<ImmutableContentFile> inventory)
    {
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return [.. inventory
            .SelectMany(file => ParentDirectories(file.RelativePath))
            .Select(relative => ResolveFile(root, relative))
            .Append(root)
            .Distinct(comparer)
            .OrderByDescending(path => path.Length)];
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

    private readonly record struct WindowsFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint NumberOfLinks,
        long SizeBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    private enum StreamInfoLevels
    {
        FindStreamInfoStandard = 0
    }

    private enum CleanupTokenInformationClass
    {
        TokenElevation = 20
    }

    private enum ServiceStatusInfo
    {
        Process = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle fileHandle,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "FindFirstStreamW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr FindFirstStream(
        string fileName,
        StreamInfoLevels infoLevel,
        out Win32FindStreamData findStreamData,
        uint reserved);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "FindNextStreamW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(
        IntPtr findStreamHandle,
        out Win32FindStreamData findStreamData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFileHandle);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCleanupTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        CleanupTokenInformationClass tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "OpenSCManagerW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceControlManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "OpenServiceW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeServiceHandle OpenWindowsService(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "QueryServiceStatusEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWindowsServiceStatus(
        SafeServiceHandle service,
        ServiceStatusInfo infoLevel,
        out ServiceStatusProcess serviceStatus,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
