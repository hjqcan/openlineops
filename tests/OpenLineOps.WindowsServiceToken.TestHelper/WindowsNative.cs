using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.WindowsServiceToken.TestHelper;

[SupportedOSPlatform("windows")]
internal static class WindowsNative
{
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x00100000;
    public const uint TokenDuplicate = 0x0002;
    public const uint TokenImpersonate = 0x0004;
    public const uint TokenQuery = 0x0008;

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint DeleteAccess = 0x00010000;
    private const uint ServiceWin32OwnProcess = 0x00000010;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceRunning = 0x00000004;
    private const uint ScStatusProcessInfo = 0;
    private const uint ServiceConfigServiceSidInfo = 5;
    private const uint ServiceSidTypeUnrestricted = 1;
    private const uint ServiceSidTypeRestricted = 3;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const int ErrorInsufficientBuffer = 122;
    private const uint GroupEnabled = 0x00000004;
    private const uint GroupUseForDenyOnly = 0x00000010;
    private const int TokenPrimary = 1;
    private const int TokenImpersonation = 2;
    private const int SecurityImpersonation = 2;
    private const int TokenElevationTypeDefault = 1;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeDevice = 0x00000040;
    private const uint FileAttributeReparsePoint = 0x00000400;

    private const string LocalServiceSid = "S-1-5-19";
    private const string ServiceLogonSid = "S-1-5-6";
    private const string AdministratorsSid = "S-1-5-32-544";

    public static void ValidateHelperAndMarkForDeletion(string helperServiceName)
    {
        var helperServiceSid = DeriveServiceSid(helperServiceName, "helper");
        using var token = OpenCurrentProcessToken(TokenQuery, "helper identity validation");
        var identity = ReadTokenEvidence(token);
        if (!string.Equals(identity.UserSid, LocalServiceSid, StringComparison.Ordinal)
            || identity.TokenType != TokenPrimary
            || identity.ElevationType != TokenElevationTypeDefault
            || identity.IsRestricted
            || identity.Groups.Any(group => string.Equals(
                group.Sid,
                AdministratorsSid,
                StringComparison.Ordinal))
            || !HasEnabledGroup(identity.Groups, ServiceLogonSid)
            || !HasEnabledGroup(identity.Groups, helperServiceSid)
            || identity.RestrictedSids.Any(group => string.Equals(
                group.Sid,
                helperServiceSid,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The token-transfer helper must run as a primary, unlinked, unrestricted LocalService Windows service token with its exact service SID and the service-logon SID enabled only in TokenGroups.");
        }

        using var manager = OpenRequiredServiceControlManager();
        using var service = OpenRequiredService(
            manager,
            helperServiceName,
            DeleteAccess | ServiceQueryConfig | ServiceQueryStatus,
            "helper");
        var configuration = ReadServiceConfiguration(service, helperServiceName);
        if (configuration.ServiceType != ServiceWin32OwnProcess
            || !string.Equals(
                configuration.ServiceStartName,
                "NT AUTHORITY\\LocalService",
                StringComparison.OrdinalIgnoreCase)
            || ReadServiceSidType(service, helperServiceName) != ServiceSidTypeUnrestricted)
        {
            throw new InvalidOperationException(
                $"Token-transfer helper service '{helperServiceName}' must be a LocalService SERVICE_SID_TYPE_UNRESTRICTED WIN32_OWN_PROCESS service.");
        }

        WaitForExactHelperServiceRunning(service, helperServiceName);

        if (!DeleteService(service))
        {
            throw NativeFailure(
                $"Could not mark token-transfer helper service '{helperServiceName}' for deletion.");
        }
    }

    private static void WaitForExactHelperServiceRunning(
        SafeServiceHandle service,
        string helperServiceName)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            var status = ReadServiceStatus(service, helperServiceName);
            if (status.CurrentState == ServiceRunning)
            {
                if (status.ServiceType != ServiceWin32OwnProcess
                    || status.ProcessId != Environment.ProcessId)
                {
                    throw new InvalidOperationException(
                        $"Token-transfer helper service '{helperServiceName}' is not bound to exact helper PID {Environment.ProcessId} in Running state.");
                }

                return;
            }

            if (status.CurrentState == ServiceStopped)
            {
                throw new InvalidOperationException(
                    $"Token-transfer helper service '{helperServiceName}' stopped before reaching Running state; Win32 exit code {status.Win32ExitCode}.");
            }

            if (status.CurrentState != ServiceStartPending)
            {
                throw new InvalidOperationException(
                    $"Token-transfer helper service '{helperServiceName}' entered unexpected state {status.CurrentState} before Running.");
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException(
            $"Token-transfer helper service '{helperServiceName}' did not reach Running state before its deadline.");
    }

    public static ValidatedSourceService OpenValidatedSourceService(
        string sourceServiceName,
        uint expectedProcessId,
        string expectedServiceSid)
    {
        var manager = OpenRequiredServiceControlManager();
        SafeServiceHandle? service = null;
        try
        {
            service = OpenRequiredService(
                manager,
                sourceServiceName,
                ServiceQueryConfig | ServiceQueryStatus,
                "source Station");
            var configuration = ReadServiceConfiguration(service, sourceServiceName);
            if (configuration.ServiceType != ServiceWin32OwnProcess
                || !string.Equals(
                    configuration.ServiceStartName,
                    "NT AUTHORITY\\LocalService",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Source Station service '{sourceServiceName}' must be a LocalService WIN32_OWN_PROCESS service.");
            }

            var sidType = ReadServiceSidType(service, sourceServiceName);
            if (sidType != ServiceSidTypeRestricted)
            {
                throw new InvalidOperationException(
                    $"Source Station service '{sourceServiceName}' must use SERVICE_SID_TYPE_RESTRICTED.");
            }

            var derivedServiceSid = DeriveServiceSid(sourceServiceName, "source Station");
            if (!string.Equals(derivedServiceSid, expectedServiceSid, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Source Station service '{sourceServiceName}' resolves to SID '{derivedServiceSid}', not requested SID '{expectedServiceSid}'.");
            }

            var result = new ValidatedSourceService(
                manager,
                service,
                sourceServiceName,
                expectedProcessId);
            manager = null!;
            service = null;
            result.EnsureRunning();
            return result;
        }
        finally
        {
            service?.Dispose();
            manager?.Dispose();
        }
    }

    public static SafeProcessHandle OpenRequiredProcess(
        uint processId,
        uint desiredAccess,
        string role)
    {
        var process = OpenProcess(desiredAccess, inheritHandle: false, processId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            process.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open exact {role} process PID {processId}; Win32 error {error}.");
        }

        return process;
    }

    public static void ValidateProcess(
        SafeProcessHandle process,
        uint processId,
        long expectedCreatedAtUtcTicks,
        string expectedExecutablePath,
        string role)
    {
        EnsureProcessAlive(process, processId, role);
        var actualCreatedAtUtcTicks = ReadProcessCreatedAtUtcTicks(process, processId, role);
        if (actualCreatedAtUtcTicks != expectedCreatedAtUtcTicks)
        {
            throw new InvalidOperationException(
                $"Exact {role} PID {processId} creation time is {actualCreatedAtUtcTicks}, not requested {expectedCreatedAtUtcTicks} UTC ticks.");
        }

        var actualExecutablePath = ReadProcessExecutablePath(process, processId, role);
        if (!string.Equals(
                actualExecutablePath,
                expectedExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Exact {role} PID {processId} runs '{actualExecutablePath}', not requested image '{expectedExecutablePath}'.");
        }
    }

    public static void ValidateCanonicalSourceExecutableHandle(
        SafeFileHandle file,
        string expectedPath)
    {
        if (file.IsInvalid || file.IsClosed)
        {
            throw new InvalidDataException(
                "The source Station executable file handle is not live.");
        }

        if (!GetFileInformationByHandle(file, out var information))
        {
            throw NativeFailure(
                "Could not inspect the source Station executable by handle.");
        }

        if ((information.FileAttributes
             & (FileAttributeDirectory | FileAttributeDevice | FileAttributeReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                "The source Station executable must be an ordinary non-reparse file.");
        }

        const int maximumWindowsPathLength = 32_768;
        var path = new char[maximumWindowsPathLength];
        var length = GetFinalPathNameByHandle(
            file,
            path,
            checked((uint)path.Length),
            flags: 0);
        if (length == 0 || length >= path.Length)
        {
            throw NativeFailure(
                "Could not resolve the source Station executable final path.");
        }

        var resolvedPath = NormalizeFinalWindowsPath(
            new string(path, 0, checked((int)length)));
        var canonicalExpectedPath = Path.GetFullPath(expectedPath);
        if (!string.Equals(
                resolvedPath,
                canonicalExpectedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The source Station executable resolves through a reparse point or path alias.");
        }
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

    public static void EnsureProcessAlive(
        SafeProcessHandle process,
        uint processId,
        string role)
    {
        var wait = WaitForSingleObject(process, milliseconds: 0);
        if (wait == WaitTimeout)
        {
            return;
        }

        if (wait == WaitFailed)
        {
            throw NativeFailure($"Could not inspect exact {role} PID {processId} liveness.");
        }

        if (wait == WaitObject0)
        {
            throw new InvalidOperationException($"Exact {role} PID {processId} has exited.");
        }

        throw new InvalidOperationException(
            $"Exact {role} PID {processId} returned unexpected wait status 0x{wait:x8}.");
    }

    public static SafeAccessTokenHandle OpenAndValidateSourceToken(
        SafeProcessHandle sourceProcess,
        string expectedServiceSid)
    {
        if (!OpenProcessToken(
                sourceProcess,
                TokenQuery | TokenDuplicate,
                out var token))
        {
            var error = Marshal.GetLastPInvokeError();
            token.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open the source Station primary token with TOKEN_QUERY | TOKEN_DUPLICATE; Win32 error {error}.");
        }

        try
        {
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
                    "The source Station token did not prove the exact primary, unlinked, restricted LocalService identity without an Administrators SID and with its enabled exact service SID in both groups and restricted SIDs.");
            }

            return token;
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    public static SafeAccessTokenHandle DuplicateSourceTokenForImpersonation(
        SafeAccessTokenHandle sourceToken,
        string expectedServiceSid)
    {
        if (!DuplicateTokenEx(
                sourceToken,
                TokenQuery | TokenImpersonate,
                IntPtr.Zero,
                SecurityImpersonation,
                TokenImpersonation,
                out var impersonationToken))
        {
            var error = Marshal.GetLastPInvokeError();
            impersonationToken.Dispose();
            throw new Win32Exception(
                error,
                $"Could not duplicate the source Station token for SecurityImpersonation; Win32 error {error}.");
        }

        try
        {
            var evidence = ReadTokenEvidence(impersonationToken);
            if (!string.Equals(evidence.UserSid, LocalServiceSid, StringComparison.Ordinal)
                || evidence.TokenType != TokenImpersonation
                || evidence.ImpersonationLevel != SecurityImpersonation
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
                    "The duplicated Station token did not preserve the exact restricted service identity at SecurityImpersonation level.");
            }

            return impersonationToken;
        }
        catch
        {
            impersonationToken.Dispose();
            throw;
        }
    }

    private static SafeAccessTokenHandle OpenCurrentProcessToken(
        uint desiredAccess,
        string purpose)
    {
        if (!OpenProcessToken(GetCurrentProcess(), desiredAccess, out var token))
        {
            var error = Marshal.GetLastPInvokeError();
            token.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open the current process token for {purpose}; Win32 error {error}.");
        }

        return token;
    }

    private static SafeServiceHandle OpenRequiredServiceControlManager()
    {
        var manager = OpenSCManager(machineName: null, databaseName: null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            manager.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open the local Service Control Manager; Win32 error {error}.");
        }

        return manager;
    }

    private static SafeServiceHandle OpenRequiredService(
        SafeServiceHandle manager,
        string serviceName,
        uint desiredAccess,
        string role)
    {
        var service = OpenService(manager, serviceName, desiredAccess);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            service.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open {role} service '{serviceName}'; Win32 error {error}.");
        }

        return service;
    }

    private static ServiceConfiguration ReadServiceConfiguration(
        SafeServiceHandle service,
        string serviceName)
    {
        _ = QueryServiceConfig(service, IntPtr.Zero, bufferSize: 0, out var requiredBytes);
        var sizingError = Marshal.GetLastPInvokeError();
        if (requiredBytes == 0 || sizingError != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(
                sizingError,
                $"Could not size service '{serviceName}' configuration; Win32 error {sizingError}.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!QueryServiceConfig(service, buffer, requiredBytes, out _))
            {
                throw NativeFailure($"Could not read service '{serviceName}' configuration.");
            }

            var configuration = Marshal.PtrToStructure<QueryServiceConfigNative>(buffer);
            return new ServiceConfiguration(
                configuration.ServiceType,
                Marshal.PtrToStringUni(configuration.ServiceStartName) ?? string.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ServiceStatusProcess ReadServiceStatus(
        SafeServiceHandle service,
        string serviceName)
    {
        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                out var status,
                checked((uint)Marshal.SizeOf<ServiceStatusProcess>()),
                out _))
        {
            throw NativeFailure($"Could not read service '{serviceName}' process status.");
        }

        return status;
    }

    private static uint ReadServiceSidType(SafeServiceHandle service, string serviceName)
    {
        if (!QueryServiceConfig2(
                service,
                ServiceConfigServiceSidInfo,
                out var sidInfo,
                checked((uint)Marshal.SizeOf<ServiceSidInfo>()),
                out _))
        {
            throw NativeFailure($"Could not read service '{serviceName}' SID type.");
        }

        return sidInfo.ServiceSidType;
    }

    private static string DeriveServiceSid(string serviceName, string role)
    {
        try
        {
            var account = new NTAccount("NT SERVICE", serviceName);
            return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException exception)
        {
            throw new InvalidOperationException(
                $"Could not derive the SID for {role} service '{serviceName}'.",
                exception);
        }
    }

    private static string ReadProcessExecutablePath(
        SafeProcessHandle process,
        uint processId,
        string role)
    {
        const int maximumWindowsPathLength = 32_768;
        var path = new char[maximumWindowsPathLength];
        var length = checked((uint)path.Length);
        if (!QueryFullProcessImageName(process, flags: 0, path, ref length) || length == 0)
        {
            throw NativeFailure($"Could not read exact {role} PID {processId} image path.");
        }

        return Path.GetFullPath(new string(path, 0, checked((int)length)));
    }

    private static long ReadProcessCreatedAtUtcTicks(
        SafeProcessHandle process,
        uint processId,
        string role)
    {
        if (!GetProcessTimes(
                process,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw NativeFailure($"Could not read exact {role} PID {processId} creation time.");
        }

        var fileTime = checked(((long)creationTime.HighDateTime << 32)
                               | creationTime.LowDateTime);
        return DateTime.FromFileTimeUtc(fileTime).Ticks;
    }

    private static TokenEvidence ReadTokenEvidence(SafeAccessTokenHandle token)
    {
        var groups = ReadTokenGroups(token, TokenInformationClass.TokenGroups);
        var restrictedSids = ReadTokenGroups(
            token,
            TokenInformationClass.TokenRestrictedSids);
        var tokenType = ReadTokenInt32(token, TokenInformationClass.TokenType);
        return new TokenEvidence(
            ReadTokenUserSid(token),
            tokenType,
            tokenType == TokenImpersonation
                ? ReadTokenInt32(token, TokenInformationClass.TokenImpersonationLevel)
                : -1,
            ReadTokenInt32(token, TokenInformationClass.TokenElevationType),
            IsTokenRestricted(token),
            groups,
            restrictedSids);
    }

    private static string ReadTokenUserSid(SafeAccessTokenHandle token)
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
        SafeAccessTokenHandle token,
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
        SafeAccessTokenHandle token,
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
        SafeAccessTokenHandle token,
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

    internal sealed class ValidatedSourceService : IDisposable
    {
        private readonly SafeServiceHandle _manager;
        private readonly SafeServiceHandle _service;
        private readonly string _serviceName;
        private readonly uint _expectedProcessId;
        private bool _disposed;

        internal ValidatedSourceService(
            SafeServiceHandle manager,
            SafeServiceHandle service,
            string serviceName,
            uint expectedProcessId)
        {
            _manager = manager;
            _service = service;
            _serviceName = serviceName;
            _expectedProcessId = expectedProcessId;
        }

        public void EnsureRunning()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var status = ReadServiceStatus(_service, _serviceName);
            if (status.ServiceType != ServiceWin32OwnProcess
                || status.CurrentState != ServiceRunning
                || status.ProcessId != _expectedProcessId)
            {
                throw new InvalidOperationException(
                    $"Source Station service '{_serviceName}' is not Running on exact PID {_expectedProcessId}.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _service.Dispose();
            _manager.Dispose();
            _disposed = true;
        }
    }

    private sealed record ServiceConfiguration(uint ServiceType, string ServiceStartName);

    private sealed record TokenEvidence(
        string UserSid,
        int TokenType,
        int ImpersonationLevel,
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

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigNative
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceSidInfo
    {
        public uint ServiceSidType;
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
        TokenImpersonationLevel = 9,
        TokenRestrictedSids = 11,
        TokenElevationType = 18
    }

    internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] executablePath,
        ref uint executablePathLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeProcessHandle handle,
        uint milliseconds);

    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle duplicateToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle manager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(
        SafeServiceHandle service,
        IntPtr serviceConfiguration,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(
        SafeServiceHandle service,
        uint informationLevel,
        out ServiceSidInfo buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        uint informationLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(SafeServiceHandle service);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
