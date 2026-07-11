using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace OpenLineOps.ProcessIsolation;

public static class WindowsAppContainerIdentity
{
    public const string ExternalProgramContentCapabilityName =
        "OpenLineOps.externalProgramContent";

    private const int ErrorFileNotFoundHResult = unchecked((int)0x80070002);
    private const int ErrorNotFoundHResult = unchecked((int)0x80070490);

    public static string EnsureProfile(string profileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer identities require Windows.");
        }

        using var capabilities = WindowsAppContainerSecurityCapabilities.Create(
            new WindowsAppContainerPolicy(profileName, NetworkAccessAllowed: false));
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
        var result = DeriveAppContainerSidFromAppContainerName(profileName, out var sidPointer);
        if (result < 0 || sidPointer == IntPtr.Zero)
        {
            throw new Win32Exception(
                result & 0xFFFF,
                $"Could not derive AppContainer profile '{profileName}'.");
        }

        string sid;
        try
        {
            sid = new SecurityIdentifier(sidPointer).Value;
        }
        finally
        {
            _ = FreeSid(sidPointer);
        }

        using var mapping = Registry.CurrentUser.OpenSubKey(
            "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion"
            + $"\\AppContainer\\Mappings\\{sid}",
            writable: false);
        return mapping is not null;
    }

    internal static string GetProfileFolderPath(string appContainerSid)
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

    private WindowsAppContainerSecurityCapabilities(
        SafeSidHandle appContainerSidHandle,
        string appContainerSid,
        WindowsCapabilitySid[] capabilitySids,
        IntPtr capabilities,
        IntPtr securityCapabilities)
    {
        _appContainerSidHandle = appContainerSidHandle;
        AppContainerSid = appContainerSid;
        _capabilitySids = capabilitySids;
        _capabilities = capabilities;
        _securityCapabilities = securityCapabilities;
    }

    public string AppContainerSid { get; }

    public IntPtr SecurityCapabilitiesPointer => _securityCapabilities != IntPtr.Zero
        ? _securityCapabilities
        : throw new ObjectDisposedException(nameof(WindowsAppContainerSecurityCapabilities));

    public static WindowsAppContainerSecurityCapabilities Create(
        WindowsAppContainerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("AppContainer security capabilities require Windows.");
        }

        return CreateWindows(policy);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsAppContainerSecurityCapabilities CreateWindows(
        WindowsAppContainerPolicy policy)
    {
        ValidateProfileName(policy.ProfileName);
        var capabilityNames = ResolveCapabilityNames(policy);
        var result = CreateAppContainerProfile(
            policy.ProfileName,
            policy.ProfileName,
            "OpenLineOps isolated external program host",
            IntPtr.Zero,
            capabilityCount: 0,
            out var sidPointer);
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
        var capabilitySids = Array.Empty<WindowsCapabilitySid>();
        IntPtr capabilities = IntPtr.Zero;
        IntPtr securityCapabilities = IntPtr.Zero;
        try
        {
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
                new SecurityIdentifier(sidPointer).Value,
                capabilitySids,
                capabilities,
                securityCapabilities);
        }
        catch
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
