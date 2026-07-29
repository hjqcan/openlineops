using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.WindowsSecurity;

namespace OpenLineOps.ProcessIsolation;

public sealed record WindowsAppContainerProfileArtifactState(
    bool PackageRootExists,
    bool ProfileDirectoryExists,
    bool MappingExists,
    bool MappingChildrenExists,
    bool StorageExists,
    bool StorageChildrenExists)
{
    public bool AnyArtifactsExist =>
        PackageRootExists
        || ProfileDirectoryExists
        || MappingExists
        || MappingChildrenExists
        || StorageExists
        || StorageChildrenExists;
}

public static class WindowsAppContainerIdentity
{
    public const string ExternalProgramContentCapabilityName =
        "OpenLineOps.externalProgramContent";

    public const string PythonRuntimeCapabilityName =
        "OpenLineOps.pythonRuntime";

    private const int ErrorFileNotFoundHResult = unchecked((int)0x80070002);
    private const int ErrorNotFoundHResult = unchecked((int)0x80070490);
    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    public static string EnsureProfile(
        string profileName,
        string? profileLifecycleManagerServiceSid = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer identities require Windows.");
        }

        using var capabilities = WindowsAppContainerSecurityCapabilities.Create(new WindowsAppContainerPolicy(
            profileName,
            NetworkAccessAllowed: false,
            ProfileLifecycleManagerServiceSid: profileLifecycleManagerServiceSid));
        return capabilities.AppContainerSid;
    }

    public static string EnsureCapabilitySid(string capabilityName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer capabilities require Windows.");
        }

        using var capability = WindowsCapabilitySid.Create(capabilityName);
        return capability.Value;
    }

    public static bool DeleteProfile(string profileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer identities require Windows.");
        }

        WindowsAppContainerSecurityCapabilities.ValidateProfileName(profileName);
        using var operation = WindowsAppContainerProfileOperationLock.Enter(profileName);
        return DeleteProfileCore(profileName);
    }

    [SupportedOSPlatform("windows")]
    internal static bool DeleteProfileCore(string profileName)
    {
        var result = DeleteAppContainerProfile(profileName);
        if (result >= 0)
        {
            return true;
        }

        if (result is ErrorFileNotFoundHResult or ErrorNotFoundHResult)
        {
            return false;
        }

        throw new Win32Exception(
            result & 0xFFFF,
            $"Could not delete AppContainer profile '{profileName}'.");
    }

    [SupportedOSPlatform("windows")]
    public static bool ProfileExists(string profileName)
    {
        WindowsAppContainerSecurityCapabilities.ValidateProfileName(profileName);
        using var operation = WindowsAppContainerProfileOperationLock.Enter(profileName);
        return ProfileExistsCore(profileName);
    }

    public static WindowsAppContainerProfileArtifactState ProbeProfileArtifacts(
        string profileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer identities require Windows.");
        }

        WindowsAppContainerSecurityCapabilities.ValidateProfileName(profileName);
        using var operation = WindowsAppContainerProfileOperationLock.Enter(profileName);
        return ProbeProfileArtifactsCore(profileName);
    }

    [SupportedOSPlatform("windows")]
    internal static bool ProfileExistsCore(string profileName) =>
        ProbeProfileArtifactsCore(profileName).AnyArtifactsExist;

    [SupportedOSPlatform("windows")]
    internal static WindowsAppContainerProfileArtifactState ProbeProfileArtifactsCore(
        string profileName)
    {
        var sid = DeriveProfileSid(profileName);

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(localAppData))
        {
            throw new InvalidDataException(
                "Current Windows identity has no absolute LocalAppData profile path.");
        }

        var packageRoot = Path.GetFullPath(Path.Combine(
            localAppData,
            "Packages",
            profileName.ToLowerInvariant()));
        var profileDirectory = Path.Combine(packageRoot, "AC");
        var mappingPath = WindowsAppContainerProfileLifecycleAccess.MappingKeyPath(sid);
        var storagePath = WindowsAppContainerProfileLifecycleAccess.StorageKeyPath(profileName);
        return new WindowsAppContainerProfileArtifactState(
            FileSystemArtifactExists(packageRoot),
            FileSystemArtifactExists(profileDirectory),
            RegistryArtifactExists(mappingPath),
            RegistryArtifactExists(mappingPath + "\\Children"),
            RegistryArtifactExists(storagePath),
            RegistryArtifactExists(storagePath + "\\Children"));
    }

    [SupportedOSPlatform("windows")]
    internal static string DeriveProfileSid(string profileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer identities require Windows.");
        }

        WindowsAppContainerSecurityCapabilities.ValidateProfileName(profileName);
        var result = DeriveAppContainerSidFromAppContainerName(profileName, out var sidPointer);
        if (sidPointer == IntPtr.Zero)
        {
            throw new Win32Exception(
                result & 0xFFFF,
                $"Could not derive AppContainer profile '{profileName}'.");
        }

        try
        {
            if (result < 0)
            {
                throw new Win32Exception(
                    result & 0xFFFF,
                    $"Could not derive AppContainer profile '{profileName}'.");
            }

            return new SecurityIdentifier(sidPointer).Value;
        }
        finally
        {
            _ = FreeSid(sidPointer);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void RollBackCreatedProfile(string profileName)
    {
        _ = DeleteProfileCore(profileName);

        var artifacts = ProbeProfileArtifactsCore(profileName);
        if (artifacts.AnyArtifactsExist)
        {
            throw new InvalidDataException(
                $"New AppContainer profile '{profileName}' was not completely rolled back.");
        }
    }

    public static string GetProfileFolderPath(string appContainerSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer identities require Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(appContainerSid);
        var result = GetAppContainerFolderPath(appContainerSid, out var pathPointer);
        if (result < 0 || pathPointer == IntPtr.Zero)
        {
            throw new Win32Exception(
                result & 0xFFFF,
                $"Could not resolve AppContainer profile folder for '{appContainerSid}'.");
        }

        try
        {
            return Marshal.PtrToStringUni(pathPointer)
                   ?? throw new InvalidDataException(
                       "AppContainer profile folder resolved to an invalid path.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int GetAppContainerFolderPath(
        string appContainerSid,
        out IntPtr path);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out IntPtr appContainerSid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr FreeSid(IntPtr sid);

    [SupportedOSPlatform("windows")]
    private static bool RegistryArtifactExists(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key is not null;
    }

    [SupportedOSPlatform("windows")]
    private static bool FileSystemArtifactExists(string path)
    {
        var attributes = GetFileAttributes(ToExtendedPath(path));
        if (attributes != InvalidFileAttributes)
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return false;
        }

        throw new Win32Exception(
            error,
            "Could not probe an AppContainer profile filesystem artifact.");
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string fileName);

}

internal static class WindowsAppContainerProfileOperationLock
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, LockEntry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static IDisposable Enter(string profileName)
    {
        LockEntry entry;
        lock (SyncRoot)
        {
            if (!Entries.TryGetValue(profileName, out entry!))
            {
                entry = new LockEntry();
                Entries.Add(profileName, entry);
            }

            entry.ReferenceCount = checked(entry.ReferenceCount + 1);
        }

        try
        {
            entry.Gate.Wait();
            return new LockLease(profileName, entry);
        }
        catch
        {
            ReleaseReference(profileName, entry);
            throw;
        }
    }

    private static void ReleaseReference(string profileName, LockEntry entry)
    {
        lock (SyncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                _ = Entries.Remove(profileName);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class LockEntry
    {
        public SemaphoreSlim Gate { get; } = new(initialCount: 1, maxCount: 1);

        public int ReferenceCount { get; set; }
    }

    private sealed class LockLease : IDisposable
    {
        private readonly string _profileName;
        private LockEntry? _entry;

        public LockLease(string profileName, LockEntry entry)
        {
            _profileName = profileName;
            _entry = entry;
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null)
            {
                return;
            }

            _ = entry.Gate.Release();
            ReleaseReference(_profileName, entry);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityCapabilities
{
    public IntPtr AppContainerSid;
    public IntPtr Capabilities;
    public uint CapabilityCount;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SidAndAttributes
{
    public IntPtr Sid;
    public uint Attributes;
}

internal enum WindowsAppContainerProfileConfigurationCheckpoint
{
    ProfileCreated,
    ConfigurationEntered,
    PersistentConfigurationCompleted
}

internal sealed class WindowsAppContainerSecurityCapabilities : IDisposable
{
    private const int ErrorAlreadyExistsHResult = unchecked((int)0x800700B7);
    private const uint SeGroupEnabled = 0x00000004;
    private const string InternetClientCapabilityName = "internetClient";
    private const int MaximumCapabilities = 16;

    private SafeSidHandle? _appContainerSidHandle;
    private WindowsCapabilitySid[] _capabilitySids;
    private IntPtr _capabilities;
    private IntPtr _securityCapabilities;
    private IDisposable? _profileOperation;

    private WindowsAppContainerSecurityCapabilities(
        SafeSidHandle appContainerSidHandle,
        string appContainerSid,
        WindowsCapabilitySid[] capabilitySids,
        IntPtr capabilities,
        IntPtr securityCapabilities,
        IDisposable profileOperation)
    {
        _appContainerSidHandle = appContainerSidHandle;
        AppContainerSid = appContainerSid;
        _capabilitySids = capabilitySids;
        _capabilities = capabilities;
        _securityCapabilities = securityCapabilities;
        _profileOperation = profileOperation;
    }

    public string AppContainerSid { get; }

    public IntPtr SecurityCapabilitiesPointer => _securityCapabilities != IntPtr.Zero
        ? _securityCapabilities
        : throw new ObjectDisposedException(nameof(WindowsAppContainerSecurityCapabilities));

    public static WindowsAppContainerSecurityCapabilities Create(
        WindowsAppContainerPolicy policy)
        => Create(policy, checkpoint: null);

    internal static WindowsAppContainerSecurityCapabilities Create(
        WindowsAppContainerPolicy policy,
        Action<WindowsAppContainerProfileConfigurationCheckpoint>? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer security capabilities require Windows.");
        }

        return CreateWindows(policy, checkpoint);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsAppContainerSecurityCapabilities CreateWindows(
        WindowsAppContainerPolicy policy,
        Action<WindowsAppContainerProfileConfigurationCheckpoint>? checkpoint)
    {
        ValidateProfileName(policy.ProfileName);
        WindowsAppContainerProfileLifecycleAccess.ValidateManagerServiceSid(
            policy.ProfileLifecycleManagerServiceSid);
        var capabilityNames = ResolveCapabilityNames(policy);
        var operation = WindowsAppContainerProfileOperationLock.Enter(
            policy.ProfileName);
        try
        {
            return CreateWindowsUnderLock(
                policy,
                capabilityNames,
                checkpoint,
                operation);
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsAppContainerSecurityCapabilities CreateWindowsUnderLock(
        WindowsAppContainerPolicy policy,
        string[] capabilityNames,
        Action<WindowsAppContainerProfileConfigurationCheckpoint>? checkpoint,
        IDisposable operation)
    {
        var result = CreateAppContainerProfile(
            policy.ProfileName,
            policy.ProfileName,
            "OpenLineOps isolated external program host",
            IntPtr.Zero,
            capabilityCount: 0,
            out var sidPointer);
        var createdNew = result >= 0;
        if (result == ErrorAlreadyExistsHResult)
        {
            result = DeriveAppContainerSidFromAppContainerName(
                policy.ProfileName,
                out sidPointer);
        }

        if (result < 0 || sidPointer == IntPtr.Zero)
        {
            throw new Win32Exception(
                result & 0xFFFF,
                $"Could not create or resolve AppContainer profile '{policy.ProfileName}'.");
        }

        var appContainerSidHandle = new SafeSidHandle(sidPointer);
        var appContainerSid = string.Empty;
        var capabilitySids = Array.Empty<WindowsCapabilitySid>();
        IntPtr capabilities = IntPtr.Zero;
        IntPtr securityCapabilities = IntPtr.Zero;
        try
        {
            appContainerSid = new SecurityIdentifier(sidPointer).Value;
            if (createdNew)
            {
                checkpoint?.Invoke(
                    WindowsAppContainerProfileConfigurationCheckpoint.ProfileCreated);
            }

            checkpoint?.Invoke(
                WindowsAppContainerProfileConfigurationCheckpoint.ConfigurationEntered);
            if (policy.ProfileLifecycleManagerServiceSid is not null)
            {
                WindowsAppContainerProfileLifecycleAccess.Grant(
                    policy.ProfileName,
                    appContainerSid,
                    policy.ProfileLifecycleManagerServiceSid);
            }

            checkpoint?.Invoke(
                WindowsAppContainerProfileConfigurationCheckpoint.PersistentConfigurationCompleted);
            capabilitySids = capabilityNames
                .Select(WindowsCapabilitySid.Create)
                .ToArray();
            if (capabilitySids.Length > 0)
            {
                var stride = Marshal.SizeOf<SidAndAttributes>();
                capabilities = Marshal.AllocHGlobal(
                    checked(capabilitySids.Length * stride));
                for (var index = 0; index < capabilitySids.Length; index++)
                {
                    Marshal.StructureToPtr(
                        new SidAndAttributes
                        {
                            Sid = capabilitySids[index].Pointer,
                            Attributes = SeGroupEnabled
                        },
                        IntPtr.Add(capabilities, checked(index * stride)),
                        fDeleteOld: false);
                }
            }

            securityCapabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(
                new SecurityCapabilities
                {
                    AppContainerSid = sidPointer,
                    Capabilities = capabilities,
                    CapabilityCount = checked((uint)capabilitySids.Length)
                },
                securityCapabilities,
                fDeleteOld: false);
            return new WindowsAppContainerSecurityCapabilities(
                appContainerSidHandle,
                appContainerSid,
                capabilitySids,
                capabilities,
                securityCapabilities,
                operation);
        }
        catch (Exception configurationException)
        {
            if (securityCapabilities != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(securityCapabilities);
            }

            if (capabilities != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(capabilities);
            }

            foreach (var capabilitySid in capabilitySids)
            {
                capabilitySid.Dispose();
            }

            appContainerSidHandle.Dispose();
            if (createdNew)
            {
                try
                {
                    WindowsAppContainerIdentity.RollBackCreatedProfile(
                        policy.ProfileName);
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidOperationException(
                        $"AppContainer profile '{policy.ProfileName}' configuration failed and its rollback also failed.",
                        new AggregateException(
                            configurationException,
                            rollbackException));
                }
            }

            throw;
        }
    }

    public void Dispose()
    {
        var securityCapabilities = Interlocked.Exchange(ref _securityCapabilities, IntPtr.Zero);
        if (securityCapabilities != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(securityCapabilities);
        }

        var capabilities = Interlocked.Exchange(ref _capabilities, IntPtr.Zero);
        if (capabilities != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(capabilities);
        }

        var capabilitySids = Interlocked.Exchange(
            ref _capabilitySids,
            Array.Empty<WindowsCapabilitySid>());
        foreach (var capabilitySid in capabilitySids)
        {
            capabilitySid.Dispose();
        }

        Interlocked.Exchange(ref _appContainerSidHandle, null)?.Dispose();
        Interlocked.Exchange(ref _profileOperation, null)?.Dispose();
    }

    internal static void ValidateProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)
            || profileName.Length > 64
            || char.IsWhiteSpace(profileName[0])
            || char.IsWhiteSpace(profileName[^1])
            || profileName.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException(
                "AppContainer profile name must be a canonical portable identity of at most 64 characters.",
                nameof(profileName));
        }
    }

    private static string[] ResolveCapabilityNames(WindowsAppContainerPolicy policy)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in policy.AdditionalCapabilityNames ?? [])
        {
            if (!IsCanonicalCapabilityName(name) || !names.Add(name))
            {
                throw new ArgumentException(
                    "AppContainer capability names must be canonical and unique.",
                    nameof(policy));
            }
        }

        if (policy.NetworkAccessAllowed)
        {
            _ = names.Add(InternetClientCapabilityName);
        }

        if (names.Count > MaximumCapabilities)
        {
            throw new ArgumentException(
                $"AppContainer capability count cannot exceed {MaximumCapabilities}.",
                nameof(policy));
        }

        return names.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsCanonicalCapabilityName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1])
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(
        string appContainerName,
        string displayName,
        string description,
        IntPtr capabilities,
        uint capabilityCount,
        out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out IntPtr appContainerSid);

    private sealed class SafeSidHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeSidHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => FreeSid(handle) == IntPtr.Zero;

        [DllImport("advapi32.dll")]
        private static extern IntPtr FreeSid(IntPtr sid);
    }
}

internal static class WindowsAppContainerProfileLifecycleAccess
{
    private const int MaximumProfileEntries = 4_096;
    private const int MaximumProfileDepth = 32;
    private const uint MaximumSecurityDescriptorLength = 65_536;
    private const uint ErrorSuccess = 0;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint BootstrapFileAccess =
        ReadControl | WriteDac | FileReadAttributes;
    private const uint OwnershipFileAccess =
        ReadControl | WriteOwner | FileReadAttributes;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint SeFileObject = 1;
    private const string ServiceSidPrefix = "S-1-5-80-";
    private const string AppContainerSidPrefix = "S-1-15-2-";
    internal const string MappingRegistryPrefix =
        "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion"
        + "\\AppContainer\\Mappings";
    internal const string StorageRegistryPrefix =
        "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion"
        + "\\AppContainer\\Storage";

    [SupportedOSPlatform("windows")]
    public static void Grant(
        string profileName,
        string appContainerSid,
        string managerServiceSid)
    {
        WindowsAppContainerSecurityCapabilities.ValidateProfileName(profileName);
        var appContainerIdentity = ParseCanonicalAppContainerSid(appContainerSid);
        var managerIdentity = ParseCanonicalManagerServiceSid(managerServiceSid);
        var packageRoot = ResolveProfilePackageRoot(
            profileName,
            appContainerIdentity.Value);
        GrantProfileTreeAccess(
            packageRoot,
            managerIdentity,
            profileName);

        var mappingPath = MappingKeyPath(appContainerIdentity.Value);
        GrantRegistryAccess(
            mappingPath,
            managerIdentity,
            profileName,
            "mapping");
        string moniker;
        using (var mappingKey = Registry.CurrentUser.OpenSubKey(
                   mappingPath,
                   RegistryKeyPermissionCheck.ReadSubTree,
                   RegistryRights.QueryValues | RegistryRights.ReadPermissions))
        {
            moniker = mappingKey?.GetValue(
                    "Moniker",
                    defaultValue: null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                ?? throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' mapping has no moniker.");
        }

        var expectedMoniker = profileName.ToLowerInvariant();
        if (!string.Equals(moniker, expectedMoniker, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"AppContainer profile '{profileName}' mapping has an unexpected moniker.");
        }

        GrantRegistryAccess(
            mappingPath + "\\Children",
            managerIdentity,
            profileName,
            "mapping children");
        var storagePath = StorageKeyPath(profileName);
        GrantRegistryAccess(
            storagePath,
            managerIdentity,
            profileName,
            "storage");
        GrantRegistryAccess(
            storagePath + "\\Children",
            managerIdentity,
            profileName,
            "storage children");
    }

    [SupportedOSPlatform("windows")]
    public static void ValidateManagerServiceSid(string? value)
    {
        if (value is not null)
        {
            var identity = ParseCanonicalManagerServiceSid(value);
            _ = WindowsStationServiceIdentityReader.ReadRequired(identity.Value);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static string MappingKeyPath(string appContainerSid) =>
        MappingRegistryPrefix + "\\" + ParseCanonicalAppContainerSid(appContainerSid).Value;

    internal static string StorageKeyPath(string profileName)
    {
        WindowsAppContainerSecurityCapabilities.ValidateProfileName(profileName);
        return StorageRegistryPrefix + "\\" + profileName.ToLowerInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static string ResolveProfilePackageRoot(
        string profileName,
        string appContainerSid)
    {
        var profilePath = WindowsAppContainerIdentity.GetProfileFolderPath(appContainerSid);
        if (!Path.IsPathFullyQualified(profilePath))
        {
            throw new InvalidDataException(
                "AppContainer profile directory must be an absolute path.");
        }

        var canonicalPath = Path.GetFullPath(profilePath);
        if (!string.Equals(
                canonicalPath,
                profilePath.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "AppContainer profile directory must be a canonical path.");
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(localAppData))
        {
            throw new InvalidDataException(
                "Current Windows identity has no absolute LocalAppData profile path.");
        }

        var packagesRoot = Path.GetFullPath(Path.Combine(localAppData, "Packages"));
        var expectedPackageRoot = Path.GetFullPath(Path.Combine(
            packagesRoot,
            profileName.ToLowerInvariant()));
        var expectedProfilePath = Path.GetFullPath(Path.Combine(
            expectedPackageRoot,
            "AC"));
        if (!string.Equals(
                canonicalPath,
                expectedProfilePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "AppContainer profile directory must be the AC child of its exact LocalAppData package.");
        }

        return expectedPackageRoot;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier ParseCanonicalAppContainerSid(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        SecurityIdentifier identity;
        try
        {
            identity = new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "AppContainer profile SID must be a canonical AppContainer identity.",
                nameof(value),
                exception);
        }

        if (!value.StartsWith(AppContainerSidPrefix, StringComparison.Ordinal)
            || !string.Equals(identity.Value, value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AppContainer profile SID must be a canonical AppContainer identity.",
                nameof(value));
        }

        return identity;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier ParseCanonicalManagerServiceSid(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split('-');
        if (!value.StartsWith(ServiceSidPrefix, StringComparison.Ordinal)
            || parts.Length != 9
            || parts[0] != "S"
            || parts[1] != "1"
            || parts[2] != "5"
            || parts[3] != "80"
            || parts.Skip(4).Any(part =>
                !uint.TryParse(
                    part,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var subAuthority)
                || !string.Equals(
                    subAuthority.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    part,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "AppContainer profile lifecycle manager must be an exact canonical Windows service SID.",
                nameof(value));
        }

        SecurityIdentifier identity;
        try
        {
            identity = new SecurityIdentifier(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "AppContainer profile lifecycle manager must be an exact canonical Windows service SID.",
                nameof(value),
                exception);
        }

        if (!string.Equals(identity.Value, value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AppContainer profile lifecycle manager must be an exact canonical Windows service SID.",
                nameof(value));
        }

        return identity;
    }

    [SupportedOSPlatform("windows")]
    private static void GrantProfileTreeAccess(
        string packageRoot,
        SecurityIdentifier managerIdentity,
        string profileName)
    {
        var entries = OpenAndValidateProfileTree(packageRoot, profileName);
        try
        {
            VerifyStableProfileTree(entries, profileName);
            foreach (var entry in entries)
            {
                GrantEntryManagerAccess(entry, managerIdentity, profileName);
                VerifyStableEntry(entry, profileName);
            }

            foreach (var entry in entries)
            {
                using var ownershipEntry = OpenStableEntry(
                    entry.Path,
                    entry.Depth,
                    profileName,
                    OwnershipFileAccess);
                if (ownershipEntry.Snapshot != entry.Snapshot)
                {
                    throw new InvalidDataException(
                        $"AppContainer profile '{profileName}' entry changed between access and ownership bootstrap.");
                }

                SetEntryOwner(ownershipEntry, managerIdentity, profileName);
                VerifyStableEntry(ownershipEntry, profileName);
            }

            VerifyStableProfileTree(entries, profileName);
        }
        finally
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                entries[index].Dispose();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void GrantProfileTreeAccessForTesting(
        string packageRoot,
        SecurityIdentifier managerIdentity,
        string profileName)
    {
        ArgumentNullException.ThrowIfNull(managerIdentity);
        WindowsAppContainerSecurityCapabilities.ValidateProfileName(profileName);
        GrantProfileTreeAccess(
            Path.GetFullPath(packageRoot),
            managerIdentity,
            profileName);
    }

    internal static (uint Bootstrap, uint Ownership) ProfileAccessMasksForTesting() =>
        (BootstrapFileAccess, OwnershipFileAccess);

    [SupportedOSPlatform("windows")]
    internal static void GrantRegistryAccessForTesting(
        string keyPath,
        SecurityIdentifier managerIdentity,
        string profileName,
        string leafName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentNullException.ThrowIfNull(managerIdentity);
        WindowsAppContainerSecurityCapabilities.ValidateProfileName(profileName);
        GrantRegistryAccess(
            keyPath,
            managerIdentity,
            profileName,
            leafName);
    }

    [SupportedOSPlatform("windows")]
    private static List<StableProfileEntry> OpenAndValidateProfileTree(
        string packageRoot,
        string profileName)
    {
        var canonicalRoot = Path.GetFullPath(packageRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        var rootPrefix = canonicalRoot + Path.DirectorySeparatorChar;
        var entries = new List<StableProfileEntry>();
        try
        {
            var root = OpenStableEntry(
                canonicalRoot,
                depth: 0,
                profileName,
                BootstrapFileAccess);
            entries.Add(root);
            if (!root.IsDirectory)
            {
                throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' package root is not a directory.");
            }

            var rootIdentity = root.Snapshot.Identity;
            var identities = new HashSet<StableFileIdentity> { rootIdentity };
            for (var directoryIndex = 0;
                 directoryIndex < entries.Count;
                 directoryIndex++)
            {
                var directory = entries[directoryIndex];
                if (!directory.IsDirectory)
                {
                    continue;
                }

                if (directory.Depth >= MaximumProfileDepth)
                {
                    if (Directory.EnumerateFileSystemEntries(directory.Path).Any())
                    {
                        throw new InvalidDataException(
                            $"AppContainer profile '{profileName}' exceeds the bounded lifecycle depth.");
                    }

                    directory.SetChildren([]);
                    continue;
                }

                var children = Directory.EnumerateFileSystemEntries(
                        directory.Path,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (children.Length != children
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count())
                {
                    throw new InvalidDataException(
                        $"AppContainer profile '{profileName}' contains duplicate directory entries.");
                }

                directory.SetChildren(children);
                foreach (var childPath in children)
                {
                    if (entries.Count >= MaximumProfileEntries)
                    {
                        throw new InvalidDataException(
                            $"AppContainer profile '{profileName}' exceeds the bounded lifecycle entry count.");
                    }

                    if (!string.Equals(
                            Path.GetDirectoryName(childPath),
                            directory.Path,
                            StringComparison.OrdinalIgnoreCase)
                        || !childPath.StartsWith(
                            rootPrefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"AppContainer profile '{profileName}' contains an entry outside its package root.");
                    }

                    var child = OpenStableEntry(
                        childPath,
                        checked(directory.Depth + 1),
                        profileName,
                        BootstrapFileAccess);
                    if (child.Snapshot.Identity.VolumeSerialNumber
                        != rootIdentity.VolumeSerialNumber)
                    {
                        child.Dispose();
                        throw new InvalidDataException(
                            $"AppContainer profile '{profileName}' crosses a volume boundary.");
                    }

                    if (!identities.Add(child.Snapshot.Identity))
                    {
                        child.Dispose();
                        throw new InvalidDataException(
                            $"AppContainer profile '{profileName}' contains duplicate file identities.");
                    }

                    entries.Add(child);
                }
            }

            return entries;
        }
        catch
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                entries[index].Dispose();
            }

            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static StableProfileEntry OpenStableEntry(
        string path,
        int depth,
        string profileName,
        uint desiredAccess,
        int retryCount = 20)
    {
        SafeFileHandle handle;
        var attempts = 0;
        while (true)
        {
            handle = CreateFile(
                ToExtendedPath(path),
                desiredAccess,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                break;
            }

            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is not ErrorSharingViolation and not ErrorLockViolation
                || ++attempts >= retryCount)
            {
                throw new Win32Exception(
                    error,
                    $"Could not open AppContainer profile '{profileName}' entry by stable handle.");
            }

            Thread.Sleep(25);
        }

        try
        {
            var canonicalPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar);
            var snapshot = ReadStableSnapshot(handle);
            if (!string.Equals(
                    snapshot.FinalPath,
                    canonicalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' entry resolved outside its canonical path.");
            }

            if ((snapshot.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' cannot contain reparse points.");
            }

            if (snapshot.LinkCount != 1)
            {
                throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' entries must have exactly one hard link.");
            }

            if (snapshot.Identity.FileIdLow == 0
                && snapshot.Identity.FileIdHigh == 0)
            {
                throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' entry has no stable file identity.");
            }

            return new StableProfileEntry(
                canonicalPath,
                depth,
                handle,
                snapshot);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyStableProfileTree(
        IReadOnlyCollection<StableProfileEntry> entries,
        string profileName)
    {
        foreach (var entry in entries)
        {
            VerifyStableEntry(entry, profileName);
            if (!entry.IsDirectory)
            {
                continue;
            }

            var currentChildren = Directory.EnumerateFileSystemEntries(
                    entry.Path,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!currentChildren.SequenceEqual(
                    entry.Children,
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' changed while lifecycle access was being configured.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyStableEntry(
        StableProfileEntry entry,
        string profileName)
    {
        var current = ReadStableSnapshot(entry.Handle);
        if (current != entry.Snapshot)
        {
            throw new InvalidDataException(
                $"AppContainer profile '{profileName}' entry identity changed while lifecycle access was being configured.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantEntryManagerAccess(
        StableProfileEntry entry,
        SecurityIdentifier managerIdentity,
        string profileName)
    {
        var security = ReadEntrySecurity(entry, profileName);
        security.PurgeAccessRules(managerIdentity);
        security.AddAccessRule(new FileSystemAccessRule(
            managerIdentity,
            FileSystemRights.FullControl,
            entry.IsDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        WriteEntrySecurity(
            entry,
            security,
            DaclSecurityInformation,
            profileName);
        VerifyEntryAccess(
            entry,
            managerIdentity,
            requireManagerOwner: false,
            profileName);
    }

    [SupportedOSPlatform("windows")]
    private static void SetEntryOwner(
        StableProfileEntry entry,
        SecurityIdentifier managerIdentity,
        string profileName)
    {
        var security = ReadEntrySecurity(entry, profileName);
        security.SetOwner(managerIdentity);
        WriteEntrySecurity(
            entry,
            security,
            OwnerSecurityInformation,
            profileName);
        VerifyEntryAccess(
            entry,
            managerIdentity,
            requireManagerOwner: true,
            profileName);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemSecurity ReadEntrySecurity(
        StableProfileEntry entry,
        string profileName)
    {
        var result = GetSecurityInfo(
            entry.Handle,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var descriptor);
        if (result != ErrorSuccess || descriptor == IntPtr.Zero)
        {
            throw new Win32Exception(
                checked((int)result),
                $"Could not read AppContainer profile '{profileName}' entry security by stable handle.");
        }

        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length == 0 || length > MaximumSecurityDescriptorLength)
            {
                throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' entry has an invalid security descriptor.");
            }

            var bytes = new byte[checked((int)length)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            FileSystemSecurity security = entry.IsDirectory
                ? new DirectorySecurity()
                : new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(bytes);
            return security;
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteEntrySecurity(
        StableProfileEntry entry,
        FileSystemSecurity security,
        uint securityInformation,
        string profileName)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var pointer = pin.AddrOfPinnedObject();
            var owner = IntPtr.Zero;
            if ((securityInformation & OwnerSecurityInformation) != 0
                && (!GetSecurityDescriptorOwner(
                        pointer,
                        out owner,
                        out _)
                    || owner == IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not read AppContainer profile '{profileName}' entry owner.");
            }

            var dacl = IntPtr.Zero;
            if ((securityInformation & DaclSecurityInformation) != 0
                && (!GetSecurityDescriptorDacl(
                        pointer,
                        out var daclPresent,
                        out dacl,
                        out _)
                    || !daclPresent
                    || dacl == IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not read AppContainer profile '{profileName}' entry DACL.");
            }

            var result = SetSecurityInfo(
                entry.Handle,
                SeFileObject,
                securityInformation,
                owner,
                IntPtr.Zero,
                dacl,
                IntPtr.Zero);
            if (result != ErrorSuccess)
            {
                throw new Win32Exception(
                    checked((int)result),
                    $"Could not write AppContainer profile '{profileName}' entry security by stable handle.");
            }
        }
        finally
        {
            pin.Free();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyEntryAccess(
        StableProfileEntry entry,
        SecurityIdentifier managerIdentity,
        bool requireManagerOwner,
        string profileName)
    {
        var security = ReadEntrySecurity(entry, profileName);
        if (requireManagerOwner
            && (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !string.Equals(
                    owner.Value,
                    managerIdentity.Value,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"AppContainer profile '{profileName}' entry is not owned by its exact lifecycle manager.");
        }

        var rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule =>
                rule.IdentityReference is SecurityIdentifier identity
                && string.Equals(
                    identity.Value,
                    managerIdentity.Value,
                    StringComparison.Ordinal))
            .ToArray();
        if (rules.Length != 1
            || rules[0].AccessControlType != AccessControlType.Allow
            || (rules[0].FileSystemRights & FileSystemRights.FullControl)
            != FileSystemRights.FullControl
            || rules[0].InheritanceFlags != (entry.IsDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None)
            || rules[0].PropagationFlags != PropagationFlags.None)
        {
            throw new InvalidDataException(
                $"AppContainer profile '{profileName}' entry does not grant its exact lifecycle manager full leaf access.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static StableFileSnapshot ReadStableSnapshot(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read AppContainer profile entry link information.");
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfoClass,
                out var fileId,
                checked((uint)Marshal.SizeOf<FileIdInformation>())))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not read AppContainer profile entry stable file identity.");
        }

        return new StableFileSnapshot(
            new StableFileIdentity(
                fileId.VolumeSerialNumber,
                fileId.FileIdLow,
                fileId.FileIdHigh),
            information.NumberOfLinks,
            (FileAttributes)information.FileAttributes,
            GetFinalPath(handle));
    }

    [SupportedOSPlatform("windows")]
    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new char[capacity];
            var length = GetFinalPathNameByHandle(
                handle,
                buffer,
                checked((uint)buffer.Length),
                flags: 0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not resolve AppContainer profile entry stable path.");
            }

            if (length < buffer.Length)
            {
                return Path.GetFullPath(RemoveExtendedPathPrefix(
                        new string(buffer, 0, checked((int)length))))
                    .TrimEnd(Path.DirectorySeparatorChar);
            }

            capacity = checked((int)length + 1);
        }

        throw new PathTooLongException(
            "AppContainer profile entry stable path exceeds the Windows maximum.");
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    [SupportedOSPlatform("windows")]
    private static void GrantRegistryAccess(
        string keyPath,
        SecurityIdentifier managerIdentity,
        string profileName,
        string leafName)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(
                   keyPath,
                   RegistryKeyPermissionCheck.ReadWriteSubTree,
                   RegistryRights.ReadPermissions | RegistryRights.ChangePermissions))
        {
            if (key is null)
            {
                throw new InvalidDataException(
                    $"AppContainer profile '{profileName}' has no {leafName} registry leaf.");
            }

            var access = key.GetAccessControl(AccessControlSections.Access);
            access.PurgeAccessRules(managerIdentity);
            access.AddAccessRule(new RegistryAccessRule(
                managerIdentity,
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            key.SetAccessControl(access);
            key.Flush();
        }

        using var ownedKey = Registry.CurrentUser.OpenSubKey(
            keyPath,
            RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.FullControl)
            ?? throw new InvalidDataException(
                $"AppContainer profile '{profileName}' {leafName} registry leaf disappeared.");
        var ownedAccess = ownedKey.GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        ownedAccess.SetOwner(managerIdentity);
        ownedKey.SetAccessControl(ownedAccess);
        ownedKey.Flush();
        VerifyRegistryAccess(
            ownedKey,
            managerIdentity,
            profileName,
            leafName);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyRegistryAccess(
        RegistryKey key,
        SecurityIdentifier managerIdentity,
        string profileName,
        string leafName)
    {
        var security = key.GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !string.Equals(
                owner.Value,
                managerIdentity.Value,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"AppContainer profile '{profileName}' {leafName} registry leaf is not owned by its exact lifecycle manager.");
        }

        var rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>()
            .Where(rule =>
                rule.IdentityReference is SecurityIdentifier identity
                && string.Equals(
                    identity.Value,
                    managerIdentity.Value,
                    StringComparison.Ordinal))
            .ToArray();
        if (rules.Length != 1
            || rules[0].AccessControlType != AccessControlType.Allow
            || (rules[0].RegistryRights & RegistryRights.FullControl)
            != RegistryRights.FullControl
            || rules[0].InheritanceFlags != InheritanceFlags.ContainerInherit
            || rules[0].PropagationFlags != PropagationFlags.None)
        {
            throw new InvalidDataException(
                $"AppContainer profile '{profileName}' {leafName} registry leaf does not grant its exact lifecycle manager full leaf access.");
        }
    }

    private sealed class StableProfileEntry : IDisposable
    {
        private string[] _children = [];

        public StableProfileEntry(
            string path,
            int depth,
            SafeFileHandle handle,
            StableFileSnapshot snapshot)
        {
            Path = path;
            Depth = depth;
            Handle = handle;
            Snapshot = snapshot;
        }

        public string Path { get; }

        public int Depth { get; }

        public SafeFileHandle Handle { get; }

        public StableFileSnapshot Snapshot { get; }

        public bool IsDirectory =>
            (Snapshot.Attributes & FileAttributes.Directory) != 0;

        public IReadOnlyCollection<string> Children => _children;

        public void SetChildren(string[] children)
        {
            _children = children;
        }

        public void Dispose()
        {
            Handle.Dispose();
        }
    }

    private readonly record struct StableFileIdentity(
        ulong VolumeSerialNumber,
        ulong FileIdLow,
        ulong FileIdHigh);

    private readonly record struct StableFileSnapshot(
        StableFileIdentity Identity,
        uint LinkCount,
        FileAttributes Attributes,
        string FinalPath);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    private const int FileIdInfoClass = 18;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileIdInformation information,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        uint objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorOwner(
        IntPtr securityDescriptor,
        out IntPtr owner,
        [MarshalAs(UnmanagedType.Bool)] out bool ownerDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal sealed class WindowsCapabilitySid : IDisposable
{
    private SafeLocalAllocHandle? _sid;

    [SupportedOSPlatform("windows")]
    private WindowsCapabilitySid(SafeLocalAllocHandle sid)
    {
        _sid = sid;
        Value = new SecurityIdentifier(sid.DangerousGetHandle()).Value;
    }

    public string Value { get; }

    public IntPtr Pointer => _sid is { IsClosed: false, IsInvalid: false } sid
        ? sid.DangerousGetHandle()
        : throw new ObjectDisposedException(nameof(WindowsCapabilitySid));

    [SupportedOSPlatform("windows")]
    public static WindowsCapabilitySid Create(string capabilityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityName);
        if (!DeriveCapabilitySidsFromName(
                capabilityName,
                out var groupSidArray,
                out var groupSidCount,
                out var capabilitySidArray,
                out var capabilitySidCount))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not derive AppContainer capability '{capabilityName}'.");
        }

        try
        {
            if (groupSidArray == IntPtr.Zero
                || groupSidCount != 1
                || capabilitySidArray == IntPtr.Zero
                || capabilitySidCount != 1)
            {
                throw new InvalidDataException(
                    $"AppContainer capability '{capabilityName}' did not resolve to exactly one SID.");
            }

            var capabilitySid = Marshal.ReadIntPtr(capabilitySidArray);
            if (capabilitySid == IntPtr.Zero)
            {
                throw new InvalidDataException(
                    $"AppContainer capability '{capabilityName}' resolved to an invalid SID.");
            }

            _ = LocalFree(Marshal.ReadIntPtr(groupSidArray));
            _ = LocalFree(groupSidArray);
            groupSidArray = IntPtr.Zero;
            _ = LocalFree(capabilitySidArray);
            capabilitySidArray = IntPtr.Zero;
            return new WindowsCapabilitySid(new SafeLocalAllocHandle(capabilitySid));
        }
        catch
        {
            FreeSidArray(groupSidArray, groupSidCount);
            FreeSidArray(capabilitySidArray, capabilitySidCount);
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _sid, null)?.Dispose();
    }

    private static void FreeSidArray(IntPtr array, uint count)
    {
        if (array == IntPtr.Zero)
        {
            return;
        }

        for (var index = 0u; index < count; index++)
        {
            _ = LocalFree(Marshal.ReadIntPtr(
                array,
                checked((int)index * IntPtr.Size)));
        }

        _ = LocalFree(array);
    }

    [DllImport("api-ms-win-security-base-l1-2-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeriveCapabilitySidsFromName(
        string capabilityName,
        out IntPtr capabilityGroupSids,
        out uint capabilityGroupSidCount,
        out IntPtr capabilitySids,
        out uint capabilitySidCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed class SafeLocalAllocHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeLocalAllocHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => LocalFree(handle) == IntPtr.Zero;
    }
}
