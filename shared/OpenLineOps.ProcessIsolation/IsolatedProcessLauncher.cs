namespace OpenLineOps.ProcessIsolation;

public sealed class IsolatedProcessLauncher
{
    private readonly WindowsProcessLauncher _windowsLauncher;

    public IsolatedProcessLauncher()
        : this(new WindowsProcessLauncher())
    {
    }

    internal IsolatedProcessLauncher(WindowsProcessLauncher windowsLauncher)
    {
        _windowsLauncher = windowsLauncher ?? throw new ArgumentNullException(nameof(windowsLauncher));
    }

    public IIsolatedProcess Launch(IsolatedProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "OpenLineOps process-tree isolation requires Windows Job Objects.");
        }

        return _windowsLauncher.Launch(request);
    }
}
