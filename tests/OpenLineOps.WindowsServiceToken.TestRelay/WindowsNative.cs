using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.WindowsServiceToken.TestRelay;

[SupportedOSPlatform("windows")]
internal static class WindowsNative
{
    private const int ErrorInsufficientBuffer = 122;
    private const uint GroupEnabled = 0x00000004;
    private const uint GroupUseForDenyOnly = 0x00000010;
    private const int TokenPrimary = 1;
    private const int TokenElevationTypeDefault = 1;
    private const int UserObjectInformationName = 2;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const string LocalServiceWindowStation = "Service-0x0-3e5$";
    private const string LocalServiceDesktop = "Default";
    private const string LocalServiceSid = "S-1-5-19";
    private const string ServiceLogonSid = "S-1-5-6";
    private const string AdministratorsSid = "S-1-5-32-544";

    public static void ValidateCanonicalExecutableHandle(
        SafeFileHandle file,
        string expectedPath,
        string role)
    {
        if (file.IsInvalid || file.IsClosed)
        {
            throw new InvalidDataException(
                $"The {role} executable file handle is not live.");
        }

        if (!GetFileInformationByHandle(file, out var information))
        {
            throw NativeFailure($"Could not inspect the {role} executable by handle.");
        }

        if ((information.FileAttributes
             & (FileAttributeDirectory | FileAttributeDevice | FileAttributeReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"The {role} executable must be an ordinary non-reparse file.");
        }

        var path = new char[32_768];
        var length = GetFinalPathNameByHandle(
            file,
            path,
            checked((uint)path.Length),
            flags: 0);
        if (length == 0 || length >= path.Length)
        {
            throw NativeFailure(
                $"Could not resolve the {role} executable final path.");
        }

        var resolvedPath = NormalizeFinalWindowsPath(
            new string(path, 0, checked((int)length)));
        if (!string.Equals(
                resolvedPath,
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The {role} executable resolves through a reparse point or path alias.");
        }
    }

    public static void ValidateCurrentSourceToken(string expectedServiceSid)
    {
        using var token = BorrowedCurrentProcessTokenHandle.Create();
        var evidence = ReadTokenEvidence(token);
        if (!string.Equals(evidence.UserSid, LocalServiceSid, StringComparison.Ordinal)
            || evidence.TokenType != TokenPrimary
            || evidence.ElevationType != TokenElevationTypeDefault
            || !evidence.IsRestricted
            || evidence.Groups.Any(group => string.Equals(
                group.Sid,
                AdministratorsSid,
                StringComparison.Ordinal))
            || !HasEnabledGroup(evidence.Groups, ServiceLogonSid)
            || !HasEnabledGroup(evidence.Groups, expectedServiceSid)
            || !evidence.RestrictedSids.Any(group => string.Equals(
                group.Sid,
                expectedServiceSid,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The source-token relay did not prove the exact primary, unlinked, restricted LocalService identity without an Administrators SID and with its exact service SID enabled in TokenGroups and present in TokenRestrictedSids.");
        }
    }

    public static void ValidateCurrentServiceDesktop()
    {
        using var user32 = LoadLibraryEx(
            "user32.dll",
            IntPtr.Zero,
            LoadLibrarySearchSystem32);
        if (user32.IsInvalid)
        {
            throw NativeFailure(
                "Could not load the system USER32 library after NativeAOT startup.");
        }

        var getProcessWindowStation =
            GetRequiredUser32Export<GetProcessWindowStationDelegate>(
                user32,
                "GetProcessWindowStation");
        var getThreadDesktop = GetRequiredUser32Export<GetThreadDesktopDelegate>(
            user32,
            "GetThreadDesktop");
        var getUserObjectInformation =
            GetRequiredUser32Export<GetUserObjectInformationDelegate>(
                user32,
                "GetUserObjectInformationW");

        var windowStation = getProcessWindowStation();
        if (windowStation == IntPtr.Zero)
        {
            throw NativeFailure(
                "Could not read the relay process window station.");
        }

        var desktop = getThreadDesktop(GetCurrentThreadId());
        if (desktop == IntPtr.Zero)
        {
            throw NativeFailure(
                "Could not read the relay thread desktop.");
        }

        var actualWindowStation = ReadUserObjectName(
            getUserObjectInformation,
            windowStation,
            "window station");
        var actualDesktop = ReadUserObjectName(
            getUserObjectInformation,
            desktop,
            "desktop");
        if (!string.Equals(
                actualWindowStation,
                LocalServiceWindowStation,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                actualDesktop,
                LocalServiceDesktop,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The source-token relay is attached to '{actualWindowStation}\\{actualDesktop}' instead of the exact LocalService desktop '{LocalServiceWindowStation}\\{LocalServiceDesktop}'.");
        }
    }

    private static TDelegate GetRequiredUser32Export<TDelegate>(
        SafeLibraryHandle library,
        string exportName)
        where TDelegate : Delegate
    {
        var address = GetProcAddress(library, exportName);
        if (address == IntPtr.Zero)
        {
            throw NativeFailure(
                $"Could not resolve system USER32 export '{exportName}'.");
        }

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    private static string ReadUserObjectName(
        GetUserObjectInformationDelegate getUserObjectInformation,
        IntPtr userObject,
        string role)
    {
        var sizingSucceeded = getUserObjectInformation(
            userObject,
            UserObjectInformationName,
            IntPtr.Zero,
            objectInformationLength: 0,
            out var requiredBytes);
        var sizingError = Marshal.GetLastPInvokeError();
        if (sizingSucceeded
            || sizingError != ErrorInsufficientBuffer
            || requiredBytes < sizeof(char)
            || requiredBytes > 2_048
            || requiredBytes % sizeof(char) != 0)
        {
            throw new InvalidDataException(
                $"The relay {role} name sizing returned Win32 error {sizingError} and an invalid {requiredBytes}-byte native buffer.");
        }

        using var buffer = new SafeHGlobalHandle(checked((int)requiredBytes));
        if (!getUserObjectInformation(
                userObject,
                UserObjectInformationName,
                buffer.DangerousGetHandle(),
                requiredBytes,
                out var returnedBytes))
        {
            throw NativeFailure($"Could not read the relay {role} name.");
        }

        if (returnedBytes < sizeof(char)
            || returnedBytes > requiredBytes
            || returnedBytes % sizeof(char) != 0)
        {
            throw new InvalidDataException(
                $"The relay {role} name returned an invalid {returnedBytes}-byte native buffer.");
        }

        var rawValue = Marshal.PtrToStringUni(
            buffer.DangerousGetHandle(),
            checked((int)returnedBytes / sizeof(char)));
        if (string.IsNullOrEmpty(rawValue)
            || rawValue[^1] != '\0'
            || rawValue[..^1].Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The relay {role} name is not exactly one null-terminated string.");
        }

        var value = rawValue[..^1];
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"The relay {role} name is empty or contains control characters.");
        }

        return value;
    }

    private static string NormalizeFinalWindowsPath(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    private static TokenEvidence ReadTokenEvidence(SafeHandle token)
    {
        var groups = ReadTokenGroups(token, TokenInformationClass.TokenGroups);
        var restrictedSids = ReadTokenGroups(
            token,
            TokenInformationClass.TokenRestrictedSids);
        return new TokenEvidence(
            ReadTokenUserSid(token),
            ReadTokenInt32(token, TokenInformationClass.TokenType),
            ReadTokenInt32(token, TokenInformationClass.TokenElevationType),
            restrictedSids.Count != 0,
            groups,
            restrictedSids);
    }

    private static string ReadTokenUserSid(SafeHandle token)
    {
        using var buffer = ReadTokenBuffer(token, TokenInformationClass.TokenUser);
        var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer.DangerousGetHandle());
        if (tokenUser.User.Sid == IntPtr.Zero)
        {
            throw new InvalidDataException("The inspected Windows token has no user SID.");
        }

        return new SecurityIdentifier(tokenUser.User.Sid).Value;
    }

    private static int ReadTokenInt32(
        SafeHandle token,
        TokenInformationClass informationClass)
    {
        const int bufferLength = sizeof(int);
        using var buffer = new SafeHGlobalHandle(bufferLength);
        Marshal.WriteInt32(buffer.DangerousGetHandle(), 0);
        if (!GetTokenInformation(
                token,
                informationClass,
                buffer.DangerousGetHandle(),
                bufferLength,
                out var returnedLength))
        {
            throw NativeFailure($"Could not read scalar token information {informationClass}.");
        }

        if (returnedLength != bufferLength)
        {
            throw new InvalidDataException(
                $"Token information {informationClass} returned {returnedLength} bytes instead of {bufferLength}.");
        }

        return Marshal.ReadInt32(buffer.DangerousGetHandle());
    }

    private static List<TokenGroup> ReadTokenGroups(
        SafeHandle token,
        TokenInformationClass informationClass)
    {
        using var buffer = ReadTokenBuffer(token, informationClass);
        var count = checked((uint)Marshal.ReadInt32(buffer.DangerousGetHandle()));
        var offset = Marshal.OffsetOf<TokenGroupsHeader>(
            nameof(TokenGroupsHeader.FirstGroup)).ToInt32();
        var stride = Marshal.SizeOf<SidAndAttributes>();
        var groups = new List<TokenGroup>(checked((int)count));
        for (var index = 0u; index < count; index++)
        {
            var group = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(
                buffer.DangerousGetHandle(),
                checked(offset + (int)index * stride)));
            if (group.Sid != IntPtr.Zero)
            {
                groups.Add(new TokenGroup(
                    new SecurityIdentifier(group.Sid).Value,
                    group.Attributes));
            }
        }

        return groups;
    }

    private static SafeHGlobalHandle ReadTokenBuffer(
        SafeHandle token,
        TokenInformationClass informationClass)
    {
        _ = GetTokenInformation(
            token,
            informationClass,
            IntPtr.Zero,
            tokenInformationLength: 0,
            out var requiredBytes);
        var sizingError = Marshal.GetLastPInvokeError();
        if (requiredBytes <= 0 || sizingError != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(
                sizingError,
                $"Could not size token information {informationClass}; Win32 error {sizingError}.");
        }

        var buffer = new SafeHGlobalHandle(requiredBytes);
        if (!GetTokenInformation(
                token,
                informationClass,
                buffer.DangerousGetHandle(),
                requiredBytes,
                out var returnedBytes))
        {
            var error = Marshal.GetLastPInvokeError();
            buffer.Dispose();
            throw new Win32Exception(
                error,
                $"Could not read token information {informationClass}; Win32 error {error}.");
        }

        if (returnedBytes > requiredBytes)
        {
            buffer.Dispose();
            throw new InvalidDataException(
                $"Token information {informationClass} exceeded its allocated native buffer.");
        }

        return buffer;
    }

    private static bool HasEnabledGroup(IReadOnlyList<TokenGroup> groups, string sid) =>
        groups.Any(group => string.Equals(group.Sid, sid, StringComparison.Ordinal)
                            && (group.Attributes & GroupEnabled) != 0
                            && (group.Attributes & GroupUseForDenyOnly) == 0);

    private static Win32Exception NativeFailure(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{message} Win32 error {error}.");
    }

    private sealed record TokenEvidence(
        string UserSid,
        int TokenType,
        int ElevationType,
        bool IsRestricted,
        IReadOnlyList<TokenGroup> Groups,
        IReadOnlyList<TokenGroup> RestrictedSids);

    private sealed record TokenGroup(string Sid, uint Attributes);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsHeader
    {
        public uint GroupCount;
        public SidAndAttributes FirstGroup;
    }

    private enum TokenInformationClass
    {
        TokenUser = 1,
        TokenGroups = 2,
        TokenType = 8,
        TokenRestrictedSids = 11,
        TokenElevationType = 18
    }

    private sealed class BorrowedCurrentProcessTokenHandle : SafeHandle
    {
        private BorrowedCurrentProcessTokenHandle()
            : base(IntPtr.Zero, ownsHandle: false)
        {
            SetHandle(new IntPtr(-4));
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        public static BorrowedCurrentProcessTokenHandle Create() => new();

        protected override bool ReleaseHandle() => true;
    }

    private sealed class SafeHGlobalHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeHGlobalHandle(int byteLength)
            : base(ownsHandle: true)
        {
            SetHandle(Marshal.AllocHGlobal(byteLength));
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    private sealed class SafeLibraryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeLibraryHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => FreeLibrary(handle);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate IntPtr GetProcessWindowStationDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate IntPtr GetThreadDesktopDelegate(uint threadId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool GetUserObjectInformationDelegate(
        IntPtr userObject,
        int index,
        IntPtr objectInformation,
        uint objectInformationLength,
        out uint requiredBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "LoadLibraryExW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeLibraryHandle LoadLibraryEx(
        string fileName,
        IntPtr file,
        uint flags);

    [DllImport(
        "kernel32.dll",
        BestFitMapping = false,
        CharSet = CharSet.Ansi,
        ExactSpelling = true,
        SetLastError = true,
        ThrowOnUnmappableChar = true)]
    private static extern IntPtr GetProcAddress(
        SafeLibraryHandle module,
        string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeHandle token,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
