using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.LeastPrivilegeLauncher;

internal static class RestrictedCurrentProcessLauncher
{
    internal const string Identity = "RestrictedCurrentLowIntegrity";
    internal const string WorkerFileName = "OpenLineOps.ScriptWorker.exe";

    private const uint DisableMaximumPrivileges = 0x1;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint Infinite = 0xffffffff;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint SeGroupIntegrity = 0x00000020;
    private const int TokenUserInformationClass = 1;
    private const int TokenIntegrityLevelInformationClass = 25;
    private const int TokenLogonSidInformationClass = 28;
    private const int ErrorInsufficientBuffer = 122;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private const uint LowIntegrityRid = 4096;
    private const string EveryoneSid = "S-1-1-0";
    private const string BuiltinUsersSid = "S-1-5-32-545";

    private static readonly IReadOnlyDictionary<string, string> RequiredEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_ISOLATION_MODE"] =
                "LeastPrivilegeIdentity",
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_REQUIRE_LEAST_PRIVILEGE"] =
                bool.TrueString,
            ["OPENLINEOPS_SCRIPT_WORKER_SANDBOX_IDENTITY"] = Identity
        };

    public static int Run(IReadOnlyList<string> arguments, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(error);

        if (!OperatingSystem.IsWindows())
        {
            error.WriteLine("OpenLineOps Least Privilege Launcher requires Windows.");
            return 78;
        }

        string? lowIntegrityRoot = null;
        try
        {
            var command = Parse(arguments);
            foreach (var (name, value) in command.Environment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            lowIntegrityRoot = CreateLowIntegrityRuntimeRoot();
            Environment.SetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", lowIntegrityRoot);
            Environment.SetEnvironmentVariable("TEMP", lowIntegrityRoot);
            Environment.SetEnvironmentVariable("TMP", lowIntegrityRoot);

            using var currentToken = OpenCurrentProcessToken();
            using var restrictedToken = CreateRestrictedLowIntegrityToken(currentToken);
            return LaunchAndWait(restrictedToken, command.WorkerPath, command.Arguments);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or Win32Exception)
        {
            error.WriteLine(exception.Message);
            return 78;
        }
        finally
        {
            if (lowIntegrityRoot is not null)
            {
                TryDeleteRuntimeRoot(lowIntegrityRoot);
            }
        }
    }

    private static LauncherCommand Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 8
            || !string.Equals(arguments[0], "-n", StringComparison.Ordinal)
            || !string.Equals(arguments[1], "-u", StringComparison.Ordinal)
            || !string.Equals(arguments[2], Identity, StringComparison.Ordinal)
            || !string.Equals(arguments[3], "--", StringComparison.Ordinal)
            || !string.Equals(arguments[4], "env", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Expected exactly the non-interactive RestrictedCurrentLowIntegrity worker launch protocol.");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 5;
        while (index < arguments.Count && arguments[index].Contains('=', StringComparison.Ordinal))
        {
            var assignment = arguments[index];
            var separator = assignment.IndexOf('=', StringComparison.Ordinal);
            var name = assignment[..separator];
            var value = assignment[(separator + 1)..];
            if (!RequiredEnvironment.TryGetValue(name, out var requiredValue)
                || !string.Equals(value, requiredValue, StringComparison.Ordinal)
                || !environment.TryAdd(name, value))
            {
                throw new InvalidDataException(
                    $"Unsupported least-privilege worker environment assignment '{name}'.");
            }

            index++;
        }

        if (environment.Count != RequiredEnvironment.Count
            || RequiredEnvironment.Keys.Any(name => !environment.ContainsKey(name)))
        {
            throw new InvalidDataException(
                "The least-privilege worker environment contract is incomplete.");
        }

        if (index >= arguments.Count)
        {
            throw new InvalidDataException("The least-privilege worker executable is required.");
        }

        var workerPath = CanonicalBundledWorker(arguments[index]);
        index++;
        var workerArguments = arguments.Skip(index).ToArray();
        if (workerArguments.Length != 0)
        {
            throw new InvalidDataException(
                "The bundled Python Script Worker does not accept launcher arguments.");
        }

        return new LauncherCommand(workerPath, workerArguments, environment);
    }

    private static string CanonicalBundledWorker(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || !Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException(
                "The least-privilege worker path must be canonical and absolute.");
        }

        var path = Path.GetFullPath(value);
        var bundleRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));
        if (!string.Equals(Path.GetFileName(path), WorkerFileName, StringComparison.Ordinal)
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty),
                bundleRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The launcher can execute only the co-packaged {WorkerFileName}.");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The co-packaged Python Script Worker is missing.", path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The co-packaged Python Script Worker cannot be a reparse point.");
        }

        return path;
    }

    private static SafeAccessTokenHandle OpenCurrentProcessToken()
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenAdjustDefault,
                out var token))
        {
            throw Win32("Could not open the launcher process token.");
        }

        return token;
    }

    private static SafeAccessTokenHandle CreateRestrictedLowIntegrityToken(
        SafeAccessTokenHandle currentToken)
    {
        var tokenUserBuffer = ReadTokenInformation(
            currentToken,
            TokenUserInformationClass);
        var tokenLogonSidBuffer = IntPtr.Zero;
        try
        {
            tokenLogonSidBuffer = ReadTokenInformation(
                currentToken,
                TokenLogonSidInformationClass);
            var tokenUser = Marshal.PtrToStructure<TokenUser>(tokenUserBuffer);
            var tokenLogonSid = Marshal.PtrToStructure<TokenGroupsOne>(
                tokenLogonSidBuffer);
            if (tokenLogonSid.GroupCount != 1
                || tokenLogonSid.FirstGroup.Sid == IntPtr.Zero)
            {
                throw new InvalidDataException(
                    "The current process token does not expose exactly one logon SID.");
            }
            var allocatedSids = new List<IntPtr>();
            try
            {
                allocatedSids.Add(ConvertRequiredSid(EveryoneSid));
                allocatedSids.Add(ConvertRequiredSid(BuiltinUsersSid));
                var restrictingSids = new[]
                {
                    new SidAndAttributes(tokenUser.User.Sid, 0),
                    new SidAndAttributes(tokenLogonSid.FirstGroup.Sid, 0),
                    new SidAndAttributes(allocatedSids[0], 0),
                    new SidAndAttributes(allocatedSids[1], 0)
                };
                if (!CreateRestrictedToken(
                        currentToken,
                        DisableMaximumPrivileges,
                        0,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero,
                        (uint)restrictingSids.Length,
                        restrictingSids,
                        out var restrictedToken))
                {
                    throw Win32("Could not create the restricted launcher token.");
                }

                try
                {
                    SetLowIntegrity(restrictedToken);
                    if (!IsTokenRestricted(restrictedToken))
                    {
                        throw new InvalidDataException(
                            "Windows did not mark the derived launcher token as restricted.");
                    }
                    if (ReadIntegrityRid(restrictedToken) != LowIntegrityRid)
                    {
                        throw new InvalidDataException(
                            "Windows did not apply Low Integrity to the derived launcher token.");
                    }

                    return restrictedToken;
                }
                catch
                {
                    restrictedToken.Dispose();
                    throw;
                }
            }
            finally
            {
                foreach (var allocatedSid in allocatedSids)
                {
                    _ = LocalFree(allocatedSid);
                }
            }
        }
        finally
        {
            if (tokenLogonSidBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(tokenLogonSidBuffer);
            }
            Marshal.FreeHGlobal(tokenUserBuffer);
        }
    }

    private static IntPtr ConvertRequiredSid(string value)
    {
        if (!ConvertStringSidToSid(value, out var sid))
        {
            throw Win32($"Could not create required restricting SID '{value}'.");
        }

        return sid;
    }

    private static void SetLowIntegrity(SafeAccessTokenHandle token)
    {
        if (!ConvertStringSidToSid("S-1-16-4096", out var sourceSid))
        {
            throw Win32("Could not create the Low Integrity SID.");
        }

        try
        {
            var sidLength = GetLengthSid(sourceSid);
            var labelSize = Marshal.SizeOf<TokenMandatoryLabel>();
            var buffer = Marshal.AllocHGlobal(checked(labelSize + (int)sidLength));
            try
            {
                var targetSid = IntPtr.Add(buffer, labelSize);
                if (!CopySid(sidLength, targetSid, sourceSid))
                {
                    throw Win32("Could not copy the Low Integrity SID.");
                }

                Marshal.StructureToPtr(
                    new TokenMandatoryLabel(
                        new SidAndAttributes(targetSid, SeGroupIntegrity)),
                    buffer,
                    fDeleteOld: false);
                if (!SetTokenInformation(
                        token,
                        TokenIntegrityLevelInformationClass,
                        buffer,
                        checked(labelSize + (int)sidLength)))
                {
                    throw Win32("Could not apply Low Integrity to the restricted token.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = LocalFree(sourceSid);
        }
    }

    private static uint ReadIntegrityRid(SafeAccessTokenHandle token)
    {
        var buffer = ReadTokenInformation(token, TokenIntegrityLevelInformationClass);
        try
        {
            var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
            var countPointer = GetSidSubAuthorityCount(label.Label.Sid);
            if (countPointer == IntPtr.Zero)
            {
                throw Win32("Could not read the restricted token integrity SID.");
            }

            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                throw new InvalidDataException("The restricted token integrity SID is empty.");
            }

            var ridPointer = GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
            if (ridPointer == IntPtr.Zero)
            {
                throw Win32("Could not read the restricted token integrity RID.");
            }

            return unchecked((uint)Marshal.ReadInt32(ridPointer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr ReadTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass)
    {
        _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, "Could not determine token information size.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        if (!GetTokenInformation(token, informationClass, buffer, required, out _))
        {
            var exception = Win32("Could not read token information.");
            Marshal.FreeHGlobal(buffer);
            throw exception;
        }

        return buffer;
    }

    private static int LaunchAndWait(
        SafeAccessTokenHandle token,
        string workerPath,
        IReadOnlyList<string> arguments)
    {
        var commandLineBuilder = new StringBuilder(Quote(workerPath));
        foreach (var argument in arguments)
        {
            commandLineBuilder.Append(' ').Append(Quote(argument));
        }
        var commandLine = (commandLineBuilder + "\0").ToCharArray();

        var startup = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles,
            StandardInput = GetStdHandle(StandardInputHandle),
            StandardOutput = GetStdHandle(StandardOutputHandle),
            StandardError = GetStdHandle(StandardErrorHandle)
        };
        if (!CreateProcessAsUser(
                token,
                workerPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                CreateNoWindow,
                IntPtr.Zero,
                AppContext.BaseDirectory,
                ref startup,
                out var processInformation))
        {
            throw Win32("Could not start the restricted Low Integrity Python Script Worker.");
        }

        try
        {
            var wait = WaitForSingleObject(processInformation.Process, Infinite);
            if (wait != 0)
            {
                throw Win32("Waiting for the restricted Python Script Worker failed.");
            }
            if (!GetExitCodeProcess(processInformation.Process, out var exitCode))
            {
                throw Win32("Could not read the restricted Python Script Worker exit code.");
            }

            return unchecked((int)exitCode);
        }
        finally
        {
            _ = CloseHandle(processInformation.Thread);
            _ = CloseHandle(processInformation.Process);
        }
    }

    private static string CreateLowIntegrityRuntimeRoot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidDataException("The launcher could not resolve the current user profile.");
        }

        var path = Path.Combine(
            userProfile,
            "AppData",
            "LocalLow",
            "OpenLineOps",
            "ScriptWorker",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteRuntimeRoot(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }
        if (!value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static Win32Exception Win32(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    private sealed record LauncherCommand(
        string WorkerPath,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributes(IntPtr sid, uint attributes)
    {
        public readonly IntPtr Sid = sid;
        public readonly uint Attributes = attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenUser(SidAndAttributes user)
    {
        public readonly SidAndAttributes User = user;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenGroupsOne(uint groupCount, SidAndAttributes firstGroup)
    {
        public readonly uint GroupCount = groupCount;
        public readonly SidAndAttributes FirstGroup = firstGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenMandatoryLabel(SidAndAttributes label)
    {
        public readonly SidAndAttributes Label = label;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(
        SafeAccessTokenHandle existingToken,
        uint flags,
        uint disableSidCount,
        IntPtr sidsToDisable,
        uint deletePrivilegeCount,
        IntPtr privilegesToDelete,
        uint restrictedSidCount,
        [In] SidAndAttributes[] sidsToRestrict,
        out SafeAccessTokenHandle restrictedToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        IntPtr information,
        int informationLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token,
        string applicationName,
        [In, Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSid(
        string stringSid,
        out IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetLengthSid(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopySid(uint destinationLength, IntPtr destination, IntPtr source);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
