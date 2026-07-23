using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.ProcessIsolation;

public sealed record IsolatedProcessStartRequest(
    string ExecutablePath,
    IReadOnlyCollection<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    WindowsProcessLimits Limits,
    WindowsAppContainerPolicy? AppContainerPolicy = null);

public sealed record WindowsAppContainerPolicy(
    string ProfileName,
    bool NetworkAccessAllowed,
    IReadOnlyCollection<string>? AdditionalCapabilityNames = null);

public enum WindowsProcessLaunchCheckpoint
{
    ProcessCreated = 1,
    ProcessAssignedToJob = 2
}

public sealed class WindowsProcessLauncher
{
    private readonly Action<WindowsProcessLaunchCheckpoint, int>? _checkpoint;

    public WindowsProcessLauncher()
    {
    }

    internal WindowsProcessLauncher(Action<WindowsProcessLaunchCheckpoint, int> checkpoint)
    {
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public WindowsIsolatedProcess Launch(IsolatedProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The suspended Windows process launcher requires Windows.");
        }

        ValidateRequest(request);
        using var commandLine = NativeUnicodeString.Create(
            WindowsCommandLine.Build(request.ExecutablePath, request.Arguments));
        using var environment = WindowsEnvironmentBlock.Create(request.Environment);
        using var standardInput = AnonymousPipe.CreateParentWrites();
        using var standardOutput = AnonymousPipe.CreateParentReads();
        using var standardError = AnonymousPipe.CreateParentReads();
        using var attributes = ProcessThreadAttributeList.Create(
            request.AppContainerPolicy,
            [standardInput.ChildHandle, standardOutput.ChildHandle, standardError.ChildHandle]);
        WindowsProcessJob? job = WindowsProcessJob.Create(request.Limits);

        var startupInfo = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = checked((uint)Marshal.SizeOf<StartupInfoEx>()),
                Flags = StartfUseStdHandles,
                StandardInput = standardInput.ChildHandle.DangerousGetHandle(),
                StandardOutput = standardOutput.ChildHandle.DangerousGetHandle(),
                StandardError = standardError.ChildHandle.DangerousGetHandle()
            },
            AttributeList = attributes.Pointer
        };

        SafeProcessHandle? processHandle = null;
        SafeThreadHandle? threadHandle = null;
        Process? process = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;
        FileStream? errorStream = null;
        try
        {
            if (!CreateProcess(
                    ToNativePath(request.ExecutablePath),
                    commandLine.Pointer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateSuspended
                    | CreateNoWindow
                    | CreateUnicodeEnvironment
                    | ExtendedStartupInfoPresent,
                    environment.Pointer,
                    request.WorkingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    $"Could not create suspended external program '{request.ExecutablePath}'. "
                    + $"Windows error {error}: {new Win32Exception(error).Message}");
            }

            processHandle = new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            threadHandle = new SafeThreadHandle(processInformation.Thread);
            var processId = checked((int)processInformation.ProcessId);
            _checkpoint?.Invoke(WindowsProcessLaunchCheckpoint.ProcessCreated, processId);

            job.Assign(processHandle);
            _checkpoint?.Invoke(WindowsProcessLaunchCheckpoint.ProcessAssignedToJob, processId);

            process = Process.GetProcessById(processId);
            inputStream = standardInput.TakeParentStream(FileAccess.Write);
            outputStream = standardOutput.TakeParentStream(FileAccess.Read);
            errorStream = standardError.TakeParentStream(FileAccess.Read);
            standardInput.CloseChildHandle();
            standardOutput.CloseChildHandle();
            standardError.CloseChildHandle();

            var resumeResult = ResumeThread(threadHandle);
            if (resumeResult != 1)
            {
                var error = resumeResult == uint.MaxValue
                    ? Marshal.GetLastWin32Error()
                    : 0;
                throw new Win32Exception(
                    error,
                    $"External program '{request.ExecutablePath}' did not have exactly one suspended thread.");
            }

            var launched = new WindowsIsolatedProcess(
                process,
                processHandle,
                inputStream,
                outputStream,
                errorStream,
                job);
            job = null;
            process = null;
            inputStream = null;
            outputStream = null;
            errorStream = null;
            processHandle = null;
            threadHandle.Dispose();
            threadHandle = null;
            return launched;
        }
        catch
        {
            if (processHandle is { IsClosed: false, IsInvalid: false })
            {
                _ = TerminateProcess(processHandle, LaunchFailureExitCode);
                _ = WaitForSingleObject(processHandle, TerminationWaitMilliseconds);
            }

            job?.Dispose();
            job = null;
            inputStream?.Dispose();
            outputStream?.Dispose();
            errorStream?.Dispose();
            process?.Dispose();
            throw;
        }
        finally
        {
            threadHandle?.Dispose();
            processHandle?.Dispose();
            job?.Dispose();
        }
    }

    private static void ValidateRequest(IsolatedProcessStartRequest request)
    {
        if (!Path.IsPathFullyQualified(request.ExecutablePath)
            || !string.Equals(
                request.ExecutablePath,
                Path.GetFullPath(request.ExecutablePath),
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(request.ExecutablePath))
        {
            throw new ArgumentException(
                "External program executable path must be an existing canonical absolute path.",
                nameof(request));
        }

        if (!Path.IsPathFullyQualified(request.WorkingDirectory)
            || !string.Equals(
                request.WorkingDirectory,
                Path.GetFullPath(request.WorkingDirectory),
                StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(request.WorkingDirectory)
            || request.WorkingDirectory.Length >= WindowsCurrentDirectoryLimit)
        {
            throw new ArgumentException(
                "External program working directory must be an existing canonical absolute path shorter than 260 characters.",
                nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Arguments);
        if (request.Arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new ArgumentException(
                "External program arguments cannot contain null values or NUL characters.",
                nameof(request));
        }

        WindowsEnvironmentBlock.Validate(request.Environment);
        var localAppData = request.Environment
            .Where(pair => string.Equals(
                pair.Key,
                "LOCALAPPDATA",
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .SingleOrDefault();
        if (request.AppContainerPolicy is not null
            && (string.IsNullOrWhiteSpace(localAppData)
                || !Path.IsPathFullyQualified(localAppData)))
        {
            throw new ArgumentException(
                "Windows AppContainer launch requires an absolute LOCALAPPDATA environment value.",
                nameof(request));
        }

        request.Limits.Validate();
    }

    private static string ToNativePath(string path)
    {
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal)
            || path.Length < WindowsCurrentDirectoryLimit)
        {
            return path;
        }

        return path.StartsWith(UncPathPrefix, StringComparison.Ordinal)
            ? ExtendedUncPathPrefix + path[UncPathPrefix.Length..]
            : ExtendedPathPrefix + path;
    }

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint LaunchFailureExitCode = 0xC0000142;
    private const uint TerminationWaitMilliseconds = 5_000;
    private const int WindowsCurrentDirectoryLimit = 260;
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPathPrefix = @"\\?\UNC\";
    private const string UncPathPrefix = @"\\";

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        IntPtr commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeThreadHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedBytes;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessInformation
    {
        public readonly IntPtr Process;
        public readonly IntPtr Thread;
        public readonly uint ProcessId;
        public readonly uint ThreadId;
    }

    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeThreadHandle(IntPtr handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class AnonymousPipe : IDisposable
    {
        private SafeFileHandle? _parentHandle;
        private SafeFileHandle? _childHandle;

        private AnonymousPipe(SafeFileHandle parentHandle, SafeFileHandle childHandle)
        {
            _parentHandle = parentHandle;
            _childHandle = childHandle;
        }

        public SafeFileHandle ChildHandle => _childHandle
            ?? throw new ObjectDisposedException(nameof(AnonymousPipe));

        public static AnonymousPipe CreateParentReads()
        {
            CreatePipe(out var parentRead, out var childWrite);
            try
            {
                MakeNonInheritable(parentRead);
                return new AnonymousPipe(parentRead, childWrite);
            }
            catch
            {
                parentRead.Dispose();
                childWrite.Dispose();
                throw;
            }
        }

        public static AnonymousPipe CreateParentWrites()
        {
            CreatePipe(out var childRead, out var parentWrite);
            try
            {
                MakeNonInheritable(parentWrite);
                return new AnonymousPipe(parentWrite, childRead);
            }
            catch
            {
                childRead.Dispose();
                parentWrite.Dispose();
                throw;
            }
        }

        public FileStream TakeParentStream(FileAccess access)
        {
            var handle = Interlocked.Exchange(ref _parentHandle, null)
                ?? throw new InvalidOperationException("Parent pipe stream has already been taken.");
            try
            {
                return new FileStream(handle, access, 4096, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void CloseChildHandle()
        {
            Interlocked.Exchange(ref _childHandle, null)?.Dispose();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _parentHandle, null)?.Dispose();
            Interlocked.Exchange(ref _childHandle, null)?.Dispose();
        }

        private static void CreatePipe(
            out SafeFileHandle readHandle,
            out SafeFileHandle writeHandle)
        {
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                InheritHandle = 1
            };
            if (!WindowsProcessLauncher.CreatePipe(
                    out readHandle,
                    out writeHandle,
                    ref attributes,
                    size: 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create external program standard stream pipe.");
            }
        }

        private static void MakeNonInheritable(SafeFileHandle handle)
        {
            if (!SetHandleInformation(handle, HandleFlagInherit, flags: 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not make the external program parent pipe handle non-inheritable.");
            }
        }
    }

    private sealed class ProcessThreadAttributeList : IDisposable
    {
        private IntPtr _attributeList;
        private IntPtr _handleList;

        private ProcessThreadAttributeList(
            IntPtr attributeList,
            IntPtr handleList,
            WindowsAppContainerSecurityCapabilities? appContainer)
        {
            _attributeList = attributeList;
            _handleList = handleList;
            _appContainer = appContainer;
        }

        public IntPtr Pointer => _attributeList != IntPtr.Zero
            ? _attributeList
            : throw new ObjectDisposedException(nameof(ProcessThreadAttributeList));

        public static ProcessThreadAttributeList Create(
            WindowsAppContainerPolicy? appContainerPolicy,
            SafeFileHandle[] handles)
        {
            var appContainer = appContainerPolicy is null
                ? null
                : WindowsAppContainerSecurityCapabilities.Create(appContainerPolicy);
            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(
                IntPtr.Zero,
                attributeCount: appContainer is null ? 1u : 2u,
                flags: 0,
                ref attributeListSize);
            if (attributeListSize == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not size the external program process attribute list.");
            }

            var attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            var handleList = Marshal.AllocHGlobal(checked(handles.Length * IntPtr.Size));
            var initialized = false;
            try
            {
                if (!InitializeProcThreadAttributeList(
                        attributeList,
                        attributeCount: appContainer is null ? 1u : 2u,
                        flags: 0,
                        ref attributeListSize))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not initialize the external program process attribute list.");
                }

                initialized = true;
                for (var index = 0; index < handles.Length; index++)
                {
                    Marshal.WriteIntPtr(
                        handleList,
                        checked(index * IntPtr.Size),
                        handles[index].DangerousGetHandle());
                }

                if (!UpdateProcThreadAttribute(
                        attributeList,
                        flags: 0,
                        ProcThreadAttributeHandleList,
                        handleList,
                        checked((nuint)(handles.Length * IntPtr.Size)),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not restrict external program inherited handles.");
                }

                if (appContainer is not null
                    && !UpdateProcThreadAttribute(
                        attributeList,
                        flags: 0,
                        ProcThreadAttributeSecurityCapabilities,
                        appContainer.SecurityCapabilitiesPointer,
                        checked((nuint)Marshal.SizeOf<SecurityCapabilities>()),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not apply external program AppContainer security capabilities.");
                }

                return new ProcessThreadAttributeList(attributeList, handleList, appContainer);
            }
            catch
            {
                if (initialized)
                {
                    DeleteProcThreadAttributeList(attributeList);
                }

                Marshal.FreeHGlobal(handleList);
                Marshal.FreeHGlobal(attributeList);
                appContainer?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var attributeList = Interlocked.Exchange(ref _attributeList, IntPtr.Zero);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            var handleList = Interlocked.Exchange(ref _handleList, IntPtr.Zero);
            if (handleList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handleList);
            }

            Interlocked.Exchange(ref _appContainer, null)?.Dispose();
        }

        private WindowsAppContainerSecurityCapabilities? _appContainer;
    }

    private sealed class WindowsEnvironmentBlock : IDisposable
    {
        private IntPtr _pointer;

        private WindowsEnvironmentBlock(IntPtr pointer)
        {
            _pointer = pointer;
        }

        public IntPtr Pointer => _pointer != IntPtr.Zero
            ? _pointer
            : throw new ObjectDisposedException(nameof(WindowsEnvironmentBlock));

        public static WindowsEnvironmentBlock Create(IReadOnlyDictionary<string, string> environment)
        {
            Validate(environment);
            var ordered = environment
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}");
            var block = string.Join('\0', ordered) + "\0\0";
            return new WindowsEnvironmentBlock(Marshal.StringToHGlobalUni(block));
        }

        public static void Validate(IReadOnlyDictionary<string, string> environment)
        {
            ArgumentNullException.ThrowIfNull(environment);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in environment)
            {
                if (string.IsNullOrEmpty(pair.Key)
                    || pair.Key.Contains('=')
                    || pair.Key.Contains('\0')
                    || pair.Value is null
                    || pair.Value.Contains('\0')
                    || !names.Add(pair.Key))
                {
                    throw new ArgumentException(
                        "External program environment must use unique canonical Windows names and NUL-free values.",
                        nameof(environment));
                }
            }
        }

        public void Dispose()
        {
            var pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    private sealed class NativeUnicodeString : IDisposable
    {
        private IntPtr _pointer;

        private NativeUnicodeString(IntPtr pointer)
        {
            _pointer = pointer;
        }

        public IntPtr Pointer => _pointer != IntPtr.Zero
            ? _pointer
            : throw new ObjectDisposedException(nameof(NativeUnicodeString));

        public static NativeUnicodeString Create(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return new NativeUnicodeString(Marshal.StringToHGlobalUni(value));
        }

        public void Dispose()
        {
            var pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    private const uint HandleFlagInherit = 0x00000001;
    private static readonly nuint ProcThreadAttributeHandleList = 0x00020002;
    private static readonly nuint ProcThreadAttributeSecurityCapabilities = 0x00020009;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

public interface IIsolatedProcess : IDisposable
{
    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    int Id { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    void TerminateProcessTree();
}

public sealed class WindowsIsolatedProcess : IIsolatedProcess
{
    private readonly Process _process;
    private readonly SafeProcessHandle _processHandle;
    private WindowsProcessJob? _job;
    private int _disposed;

    internal WindowsIsolatedProcess(
        Process process,
        SafeProcessHandle processHandle,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError,
        WindowsProcessJob job)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _processHandle = processHandle ?? throw new ArgumentNullException(nameof(processHandle));
        StandardInput = standardInput ?? throw new ArgumentNullException(nameof(standardInput));
        StandardOutput = standardOutput ?? throw new ArgumentNullException(nameof(standardOutput));
        StandardError = standardError ?? throw new ArgumentNullException(nameof(standardError));
        _job = job ?? throw new ArgumentNullException(nameof(job));
    }

    public Stream StandardInput { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    public int Id => _process.Id;

    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(_processHandle, out var exitCode))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read the isolated Windows process exit code.");
            }

            if (exitCode == StillActive && !_process.HasExited)
            {
                throw new InvalidOperationException(
                    "The isolated Windows process has not exited.");
            }

            return unchecked((int)exitCode);
        }
    }

    public uint ActiveProcessCount => Volatile.Read(ref _job)?.ActiveProcessCount ?? 0;

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _process.WaitForExitAsync(cancellationToken);

    public void TerminateProcessTree()
    {
        Volatile.Read(ref _job)?.Terminate();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        TerminateProcessTree();
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _process.Dispose();
        _processHandle.Dispose();
    }

    private const uint StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(
        SafeProcessHandle process,
        out uint exitCode);
}
