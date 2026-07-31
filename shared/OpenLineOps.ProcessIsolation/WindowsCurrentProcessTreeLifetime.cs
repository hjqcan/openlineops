using System.Diagnostics;

namespace OpenLineOps.ProcessIsolation;

/// <summary>
/// Keeps the current Windows process and every descendant in a kill-on-close Job Object.
/// </summary>
/// <remarks>
/// The binding is process-wide and idempotent. It intentionally has no disposal API:
/// Windows must close the Job Object handle as part of process termination so a graceful
/// owner can retain its natural exit code while any remaining descendants are terminated.
/// A crash or forced termination closes the same handle in the kernel and provides the
/// identical descendant cleanup guarantee.
/// </remarks>
public sealed class WindowsCurrentProcessTreeLifetime
{
    private static readonly object BindingLock = new();
    private static WindowsCurrentProcessTreeLifetime? boundLifetime;

    private readonly WindowsProcessJob _job;

    private WindowsCurrentProcessTreeLifetime(WindowsProcessJob job, int processId)
    {
        _job = job;
        ProcessId = processId;
    }

    /// <summary>
    /// Gets the process identity protected by this lifetime.
    /// </summary>
    public int ProcessId { get; }

    /// <summary>
    /// Gets whether the current process still owns its Job Object handle.
    /// </summary>
    public bool IsBound =>
        ProcessId == Environment.ProcessId
        && !_job.IsClosed;

    /// <summary>
    /// Binds the current process and its descendants to one process-owned,
    /// kill-on-close Windows Job Object.
    /// </summary>
    /// <returns>The single binding for the current process.</returns>
    /// <exception cref="PlatformNotSupportedException">
    /// The current operating system is not Windows.
    /// </exception>
    /// <exception cref="System.ComponentModel.Win32Exception">
    /// The current process cannot join a nested Job Object under its host policy.
    /// </exception>
    public static WindowsCurrentProcessTreeLifetime BindCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Current process-tree lifetime binding requires Windows.");
        }

        lock (BindingLock)
        {
            if (boundLifetime is not null)
            {
                return boundLifetime;
            }

            var job = WindowsProcessJob.CreateKillOnClose();
            using var process = Process.GetCurrentProcess();
            try
            {
                job.Assign(process.SafeHandle);
            }
            catch
            {
                job.Dispose();
                throw;
            }

            var lifetime = new WindowsCurrentProcessTreeLifetime(job, process.Id);
            boundLifetime = lifetime;
            return lifetime;
        }
    }
}
