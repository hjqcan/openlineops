using System.Diagnostics;
using System.Text;

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
        return OperatingSystem.IsWindows()
            ? _windowsLauncher.Launch(request)
            : LaunchNonWindows(request);
    }

    private static NonWindowsIsolatedProcess LaunchNonWindows(IsolatedProcessStartRequest request)
    {
        ValidateNonWindowsRequest(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var pair in request.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            startInfo.Environment.Add(pair.Key, pair.Value);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Could not start isolated process '{request.ExecutablePath}'.");
            }

            return new NonWindowsIsolatedProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static void ValidateNonWindowsRequest(IsolatedProcessStartRequest request)
    {
        if (request.AppContainerPolicy is not null)
        {
            throw new PlatformNotSupportedException(
                "Windows AppContainer isolation cannot be silently downgraded on this operating system.");
        }

        if (!Path.IsPathFullyQualified(request.ExecutablePath)
            || !string.Equals(
                request.ExecutablePath,
                Path.GetFullPath(request.ExecutablePath),
                StringComparison.Ordinal)
            || !File.Exists(request.ExecutablePath)
            || !Path.IsPathFullyQualified(request.WorkingDirectory)
            || !string.Equals(
                request.WorkingDirectory,
                Path.GetFullPath(request.WorkingDirectory),
                StringComparison.Ordinal)
            || !Directory.Exists(request.WorkingDirectory)
            || request.Arguments is null
            || request.Arguments.Any(argument => argument is null || argument.Contains('\0'))
            || request.Environment is null
            || request.Environment.Any(pair =>
                string.IsNullOrEmpty(pair.Key)
                || pair.Key.Contains('=')
                || pair.Key.Contains('\0')
                || pair.Value is null
                || pair.Value.Contains('\0')))
        {
            throw new ArgumentException("Isolated process request is invalid.", nameof(request));
        }

        request.Limits.Validate();
    }

    private sealed class NonWindowsIsolatedProcess : IIsolatedProcess
    {
        private readonly Process _process;
        private int _disposed;

        public NonWindowsIsolatedProcess(Process process)
        {
            _process = process;
        }

        public Stream StandardInput => _process.StandardInput.BaseStream;

        public Stream StandardOutput => _process.StandardOutput.BaseStream;

        public Stream StandardError => _process.StandardError.BaseStream;

        public int Id => _process.Id;

        public int ExitCode => _process.ExitCode;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _process.WaitForExitAsync(cancellationToken);

        public void TerminateProcessTree()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
            {
                _ = exception;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            TerminateProcessTree();
            _process.Dispose();
        }
    }
}
