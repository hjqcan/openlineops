using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.WindowsServiceToken.TestHelper;

[SupportedOSPlatform("windows")]
internal sealed class SourceTokenRelayProcess : IDisposable
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateSuspendedFlag = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint ProcessTerminate = 0x00000001;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const uint StillActive = 259;
    private const nuint ProcThreadAttributeParentProcess = 0x00020000;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;

    private readonly SafeRelayProcessHandle _process;
    private readonly SafeThreadHandle _thread;
    private readonly SafeJobHandle _job;
    private bool _disposed;
    private bool _validated;
    private bool _resumed;

    private SourceTokenRelayProcess(
        SafeRelayProcessHandle process,
        SafeThreadHandle thread,
        SafeJobHandle job,
        uint processId)
    {
        _process = process;
        _thread = thread;
        _job = job;
        ProcessId = processId;
    }

    public uint ProcessId { get; }

    public long CreatedAtUtcTicks { get; private set; }

    public static SourceTokenRelayProcess CreateSuspended(
        WindowsServiceTokenTransferRequest request,
        SafeProcessHandle sourceProcess)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceProcess);
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException(
                "The source-token relay helper requires its staged win-x64 process.");
        }

        if (!IsProcessInJob(sourceProcess, IntPtr.Zero, out var sourceIsInJob))
        {
            throw NativeFailure(
                "Could not determine whether the exact source Station belongs to a job");
        }
        if (sourceIsInJob)
        {
            throw new InvalidOperationException(
                "The source Station belongs to a job, so its inherited job contract cannot be proven safe for source-token relay creation.");
        }

        var helperServiceSid = new SecurityIdentifier(
            WindowsNative.DeriveServiceSid(request.HelperServiceName, "helper"));
        var runnerSid = new SecurityIdentifier(request.RunnerSid);
        SafeJobHandle? job = null;
        SafeRelayProcessHandle? process = null;
        SafeThreadHandle? thread = null;
        var processCreated = false;
        var processInformation = new ProcessInformation();
        try
        {
            job = CreateRelayJob();
            using var attributes = new ProcessThreadAttributeList(
                sourceProcess,
                job);
            using var processSecurity = new ProcessSecurityDescriptor(
                helperServiceSid,
                runnerSid);
            using var environment = CreateEnvironmentBlock();
            process = new SafeRelayProcessHandle();
            thread = new SafeThreadHandle();
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = checked((uint)Marshal.SizeOf<StartupInfoEx>())
                },
                AttributeList = attributes.Handle
            };
            var commandLine = (
                QuoteCommandLineArgument(request.HelperExecutablePath)
                + " --relay-request "
                + QuoteCommandLineArgument(request.RequestPath)
                + '\0').ToCharArray();
            var processAttributes = processSecurity.Attributes;
            if (!CreateProcess(
                    request.HelperExecutablePath,
                    commandLine,
                    ref processAttributes,
                    IntPtr.Zero,
                    inheritHandles: false,
                    CreateNoWindow
                    | CreateSuspendedFlag
                    | CreateUnicodeEnvironment
                    | ExtendedStartupInfoPresent,
                    environment.Handle,
                    request.HelperBundleRoot,
                    ref startup,
                    out processInformation))
            {
                throw NativeFailure(
                    "Could not create the suspended source-token relay from the exact Station parent process");
            }

            processCreated = true;
            process.Adopt(processInformation.Process);
            thread.Adopt(processInformation.Thread);
            processInformation.Process = IntPtr.Zero;
            processInformation.Thread = IntPtr.Zero;
            if (processInformation.ProcessId == 0
                || processInformation.ThreadId == 0
                || process.IsInvalid
                || thread.IsInvalid)
            {
                throw new InvalidDataException(
                    "CreateProcess returned incomplete source-token relay handles.");
            }

            var relay = new SourceTokenRelayProcess(
                process,
                thread,
                job,
                processInformation.ProcessId);
            process = null!;
            thread = null!;
            job = null!;
            return relay;
        }
        catch (Exception creationFailure)
        {
            Exception? cleanupFailure = null;
            if (processCreated)
            {
                cleanupFailure = process is not null && !process.IsInvalid
                    ? TerminateJobAndWait(
                        job,
                        process,
                        processInformation.ProcessId,
                        "failed suspended source-token relay creation")
                    : new InvalidDataException(
                        "CreateProcess succeeded without a live exact relay process handle, so relay termination cannot be proven.");
            }

            if (cleanupFailure is null)
            {
                throw;
            }

            throw new SourceTokenRelayCreationException(
                "Source-token relay creation failed and its exact job/process cleanup also failed.",
                processInformation.ProcessId,
                creationFailure,
                cleanupFailure);
        }
        finally
        {
            thread?.Dispose();
            job?.Dispose();
            process?.Dispose();
        }
    }

    public void BindCreatedAtUtcTicks(long expectedCreatedAtUtcTicks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (expectedCreatedAtUtcTicks < DateTime.UnixEpoch.Ticks
            || expectedCreatedAtUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedCreatedAtUtcTicks),
                "The runner-captured relay creation time is outside the valid UTC tick range.");
        }
        if (_validated || _resumed || CreatedAtUtcTicks != 0)
        {
            throw new InvalidOperationException(
                $"Source-token relay PID {ProcessId} creation time cannot be rebound.");
        }

        var actualCreatedAtUtcTicks = ReadCreatedAtUtcTicks(_process);
        if (actualCreatedAtUtcTicks != expectedCreatedAtUtcTicks)
        {
            throw new InvalidDataException(
                $"Source-token relay PID {ProcessId} creation time does not match the runner-retained process.");
        }

        CreatedAtUtcTicks = actualCreatedAtUtcTicks;
    }

    public void ValidateCreated(
        WindowsServiceTokenTransferRequest request,
        SafeProcessHandle sourceProcess)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceProcess);
        if (_resumed)
        {
            throw new InvalidOperationException(
                $"Source-token relay PID {ProcessId} cannot be validated after resume.");
        }
        if (_validated)
        {
            throw new InvalidOperationException(
                $"Source-token relay PID {ProcessId} was already validated.");
        }

        var wait = WaitForSingleObject(_process, milliseconds: 0);
        if (wait == WaitObject0)
        {
            throw new InvalidOperationException(
                $"Suspended source-token relay PID {ProcessId} exited before validation.");
        }
        if (wait == WaitFailed)
        {
            throw NativeFailure(
                $"Could not inspect suspended source-token relay PID {ProcessId} before validation");
        }
        if (wait != WaitTimeout)
        {
            throw new InvalidOperationException(
                $"Suspended source-token relay PID {ProcessId} returned unexpected wait status 0x{wait:x8} before validation.");
        }

        ValidateProcessImage(
            _process,
            request.HelperExecutablePath,
            request.HelperExecutableSha256);
        if (!IsProcessInJob(_process, _job, out var assignedToJob))
        {
            throw NativeFailure(
                "Could not validate the source-token relay containment job");
        }
        if (!assignedToJob)
        {
            throw new InvalidOperationException(
                "The source-token relay was not atomically assigned to its kill-on-close job.");
        }

        WindowsNative.EnsureProcessAlive(
            sourceProcess,
            request.SourceProcessId,
            "source Station");
        _validated = true;
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_resumed)
        {
            throw new InvalidOperationException(
                $"Source-token relay PID {ProcessId} was already resumed.");
        }
        if (!_validated)
        {
            throw new InvalidOperationException(
                $"Source-token relay PID {ProcessId} cannot be resumed before created-process validation.");
        }

        Exception? resumeFailure = null;
        var previousSuspendCount = ResumeThread(_thread);
        if (previousSuspendCount == uint.MaxValue)
        {
            resumeFailure = NativeFailure(
                "Could not resume the validated source-token relay thread");
        }
        else if (previousSuspendCount != 1)
        {
            resumeFailure = new InvalidOperationException(
                $"The source-token relay initial thread had suspend count {previousSuspendCount}, not exactly 1.");
        }

        if (resumeFailure is not null)
        {
            var cleanupFailure = TerminateJobAndWait(
                _job,
                _process,
                ProcessId,
                "source-token relay resume failure");
            throw cleanupFailure is null
                ? resumeFailure
                : new AggregateException(
                    "The source-token relay could not resume and its exact job/process cleanup also failed.",
                    resumeFailure,
                    cleanupFailure);
        }

        _resumed = true;
    }

    public async Task WaitForSuccessfulExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_resumed)
        {
            throw new InvalidOperationException(
                $"Source-token relay PID {ProcessId} cannot be awaited before it is resumed.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            timeout,
            TimeSpan.Zero);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wait = WaitForSingleObject(_process, milliseconds: 25);
            if (wait == WaitObject0)
            {
                var exitCode = ReadExitCode(_process);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Source-token relay PID {ProcessId} exited with code {exitCode}.");
                }

                return;
            }
            if (wait == WaitFailed)
            {
                throw NativeFailure(
                    $"Could not wait for source-token relay PID {ProcessId}");
            }
            if (wait != WaitTimeout)
            {
                throw new InvalidOperationException(
                    $"Source-token relay PID {ProcessId} returned unexpected wait status 0x{wait:x8}.");
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Source-token relay PID {ProcessId} did not exit before its deadline.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Exception? cleanupFailure;
        try
        {
            cleanupFailure = TerminateJobAndWait(
                _job,
                _process,
                ProcessId,
                "source-token relay disposal");
        }
        catch (Exception unexpectedCleanupFailure)
        {
            cleanupFailure = unexpectedCleanupFailure;
        }
        finally
        {
            _thread.Dispose();
            _job.Dispose();
            _process.Dispose();
            _disposed = true;
        }

        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }
    }

    private static Exception? TerminateJobAndWait(
        SafeJobHandle? job,
        SafeRelayProcessHandle process,
        uint processId,
        string context)
    {
        var failures = new List<Exception>();
        var initialWait = WaitForSingleObject(process, milliseconds: 0);
        if (initialWait == WaitObject0)
        {
            job?.Dispose();
            return null;
        }
        if (initialWait == WaitFailed)
        {
            failures.Add(NativeFailure(
                $"Could not inspect exact source-token relay PID {processId} before cleanup during {context}"));
        }
        else if (initialWait != WaitTimeout)
        {
            failures.Add(new InvalidOperationException(
                $"Exact source-token relay PID {processId} returned unexpected initial wait status 0x{initialWait:x8} during {context}."));
        }

        if (job is not null && !job.IsInvalid && !job.IsClosed)
        {
            if (TerminateJobObject(job, 70))
            {
                var jobWait = WaitForSingleObject(process, 10_000);
                if (jobWait == WaitObject0)
                {
                    job.Dispose();
                    return null;
                }
                if (jobWait == WaitFailed)
                {
                    failures.Add(NativeFailure(
                        $"Could not wait for exact source-token relay PID {processId} after job termination during {context}"));
                }
                else if (jobWait == WaitTimeout)
                {
                    failures.Add(new TimeoutException(
                        $"Exact source-token relay PID {processId} did not terminate within 10 seconds after job termination during {context}."));
                }
                else
                {
                    failures.Add(new InvalidOperationException(
                        $"Exact source-token relay PID {processId} returned unexpected post-job wait status 0x{jobWait:x8} during {context}."));
                }
            }
            else
            {
                failures.Add(NativeFailure(
                    $"Could not terminate the source-token relay job for PID {processId} during {context}"));
            }
        }
        else
        {
            failures.Add(new InvalidOperationException(
                $"The source-token relay job for PID {processId} was unavailable during {context}."));
        }

        if (!TerminateProcess(process, 70))
        {
            failures.Add(NativeFailure(
                $"Could not directly terminate exact source-token relay PID {processId} during {context}"));
        }

        job?.Dispose();
        var finalWait = WaitForSingleObject(process, 10_000);
        if (finalWait == WaitObject0)
        {
            return null;
        }
        if (finalWait == WaitFailed)
        {
            failures.Add(NativeFailure(
                $"Could not perform the final wait for exact source-token relay PID {processId} during {context}"));
        }
        else if (finalWait == WaitTimeout)
        {
            failures.Add(new TimeoutException(
                $"Exact source-token relay PID {processId} did not terminate within 10 seconds after direct termination and job close during {context}."));
        }
        else
        {
            failures.Add(new InvalidOperationException(
                $"Exact source-token relay PID {processId} returned unexpected final wait status 0x{finalWait:x8} during {context}."));
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(
                $"Multiple exact source-token relay cleanup operations failed during {context}.",
                failures)
        };
    }

    private static SafeJobHandle CreateRelayJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var failure = NativeFailure("Could not create the source-token relay job");
            job.Dispose();
            throw failure;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitActiveProcess | JobObjectLimitKillOnJobClose,
                ActiveProcessLimit = 1
            }
        };
        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
        {
            var failure = NativeFailure(
                "Could not configure the source-token relay job as kill-on-close with one active process");
            job.Dispose();
            throw failure;
        }

        return job;
    }

    private static ProcessEnvironmentBlock CreateEnvironmentBlock()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot)
            || !Path.IsPathFullyQualified(systemRoot))
        {
            throw new InvalidDataException(
                "The source-token relay requires a canonical SystemRoot.");
        }

        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["COMPlus_EnableDiagnostics"] = "0",
            ["DOTNET_EnableDiagnostics"] = "0",
            ["SystemRoot"] = Path.GetFullPath(systemRoot),
            ["WINDIR"] = Path.GetFullPath(systemRoot)
        };
        return new ProcessEnvironmentBlock(string.Concat(
            variables.Select(static pair => $"{pair.Key}={pair.Value}\0")) + "\0");
    }

    private static string QuoteCommandLineArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('"', StringComparison.Ordinal)
            || value.EndsWith('\\'))
        {
            throw new InvalidDataException(
                "The source-token relay received a path that cannot be quoted exactly.");
        }

        return '"' + value + '"';
    }

    private static long ReadCreatedAtUtcTicks(SafeRelayProcessHandle process)
    {
        if (!GetProcessTimes(process, out var created, out _, out _, out _))
        {
            throw NativeFailure("Could not read the source-token relay creation time");
        }

        return DateTime.FromFileTimeUtc(created.ToLong()).Ticks;
    }

    private static void ValidateProcessImage(
        SafeRelayProcessHandle process,
        string expectedPath,
        string expectedSha256)
    {
        var path = new char[32_768];
        var length = checked((uint)path.Length);
        if (!QueryFullProcessImageName(process, flags: 0, path, ref length)
            || length == 0
            || length >= path.Length)
        {
            throw NativeFailure("Could not read the source-token relay executable path");
        }

        var actualPath = Path.GetFullPath(new string(path, 0, checked((int)length)));
        if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Source-token relay image '{actualPath}' is not '{expectedPath}'.");
        }

        using var stream = new FileStream(
            actualPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.SequentialScan);
        WindowsNative.ValidateCanonicalSourceExecutableHandle(
            stream.SafeFileHandle,
            expectedPath);
        var actualSha256 = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(stream));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The source-token relay executable hash changed before resume.");
        }
    }

    private static uint ReadExitCode(SafeRelayProcessHandle process)
    {
        if (!GetExitCodeProcess(process, out var exitCode))
        {
            throw NativeFailure("Could not read the source-token relay exit code");
        }
        if (exitCode == StillActive)
        {
            throw new InvalidOperationException(
                "The source-token relay was signaled but still reports STILL_ACTIVE.");
        }

        return exitCode;
    }

    private static Win32Exception NativeFailure(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{message}; Win32 error {error}.");
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
        public ushort Reserved2Length;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public long ToLong() => ((long)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    private sealed class ProcessThreadAttributeList : IDisposable
    {
        private IntPtr _parentValue;
        private IntPtr _jobValue;
        private bool _initialized;

        public ProcessThreadAttributeList(
            SafeProcessHandle sourceProcess,
            SafeJobHandle job)
        {
            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(
                IntPtr.Zero,
                attributeCount: 2,
                flags: 0,
                ref bytes);
            if (bytes == 0)
            {
                throw NativeFailure(
                    "Could not size the source-token relay process attribute list");
            }

            try
            {
                Handle = Marshal.AllocHGlobal(checked((int)bytes));
                _parentValue = Marshal.AllocHGlobal(IntPtr.Size);
                _jobValue = Marshal.AllocHGlobal(IntPtr.Size);
                if (!InitializeProcThreadAttributeList(
                        Handle,
                        attributeCount: 2,
                        flags: 0,
                        ref bytes))
                {
                    throw NativeFailure(
                        "Could not initialize the source-token relay process attribute list");
                }
                _initialized = true;
                Marshal.WriteIntPtr(_parentValue, sourceProcess.DangerousGetHandle());
                Marshal.WriteIntPtr(_jobValue, job.DangerousGetHandle());
                Add(ProcThreadAttributeParentProcess, _parentValue);
                Add(ProcThreadAttributeJobList, _jobValue);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IntPtr Handle { get; private set; }

        public void Dispose()
        {
            var attributeList = Handle;
            Handle = IntPtr.Zero;
            if (_initialized)
            {
                DeleteProcThreadAttributeList(attributeList);
                _initialized = false;
            }
            if (attributeList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(attributeList);
            }

            var parentValue = _parentValue;
            _parentValue = IntPtr.Zero;
            if (parentValue != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parentValue);
            }

            var jobValue = _jobValue;
            _jobValue = IntPtr.Zero;
            if (jobValue != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(jobValue);
            }
        }

        private void Add(nuint attribute, IntPtr value)
        {
            if (!UpdateProcThreadAttribute(
                    Handle,
                    flags: 0,
                    attribute,
                    value,
                    checked((nuint)IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw NativeFailure(
                    $"Could not add source-token relay process attribute 0x{attribute:x}");
            }
        }
    }

    private sealed class ProcessSecurityDescriptor : IDisposable
    {
        private IntPtr _descriptor;

        public ProcessSecurityDescriptor(
            SecurityIdentifier owner,
            SecurityIdentifier runnerSid)
        {
            var dacl = new RawAcl(GenericAcl.AclRevision, capacity: 1);
            dacl.InsertAce(0, new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                checked((int)(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize)),
                runnerSid,
                isCallback: false,
                opaque: null));
            var descriptor = new RawSecurityDescriptor(
                ControlFlags.DiscretionaryAclPresent
                | ControlFlags.DiscretionaryAclProtected,
                owner,
                owner,
                systemAcl: null,
                dacl);
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            try
            {
                _descriptor = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, _descriptor, bytes.Length);
                Attributes = new SecurityAttributes
                {
                    Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                    SecurityDescriptor = _descriptor,
                    InheritHandle = false
                };
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public SecurityAttributes Attributes { get; private set; }

        public void Dispose()
        {
            var descriptor = _descriptor;
            _descriptor = IntPtr.Zero;
            if (descriptor != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(descriptor);
            }
        }
    }

    private sealed class ProcessEnvironmentBlock : IDisposable
    {
        private IntPtr _handle;

        public ProcessEnvironmentBlock(string environment)
        {
            var bytes = Encoding.Unicode.GetBytes(environment);
            try
            {
                _handle = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, _handle, bytes.Length);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IntPtr Handle => _handle;

        public void Dispose()
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handle);
            }
        }
    }

    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeThreadHandle()
            : base(ownsHandle: true)
        {
        }

        public void Adopt(IntPtr value) => SetHandle(value);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeRelayProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeRelayProcessHandle()
            : base(ownsHandle: true)
        {
        }

        public void Adopt(IntPtr value) => SetHandle(value);

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle job,
        uint informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(
        SafeJobHandle job,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeRelayProcessHandle process,
        SafeJobHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeProcessHandle process,
        IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateProcessW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        [In, Out] char[] commandLine,
        ref SecurityAttributes processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeThreadHandle thread);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeRelayProcessHandle process,
        uint flags,
        [Out] char[] executablePath,
        ref uint executablePathLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeRelayProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeRelayProcessHandle process,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(
        SafeRelayProcessHandle process,
        out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(
        SafeRelayProcessHandle process,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class SourceTokenRelayCreationException(
    string message,
    uint processId,
    Exception creationFailure,
    Exception cleanupFailure)
    : AggregateException(message, creationFailure, cleanupFailure)
{
    public uint ProcessId { get; } = processId;

    public Exception CleanupFailure { get; } = cleanupFailure;
}
