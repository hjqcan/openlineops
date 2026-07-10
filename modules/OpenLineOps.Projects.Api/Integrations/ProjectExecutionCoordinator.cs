using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Projects.Application.ProjectWorkspaces;

namespace OpenLineOps.Projects.Api.Integrations;

public interface IProjectExecutionLease : IAsyncDisposable
{
}

public interface IProjectExecutionCoordinator
{
    ValueTask<IProjectExecutionLease?> TryAcquireAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectExecutionCoordinator : IProjectExecutionCoordinator, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LeaseState> _leases = new(PathComparer);
    private bool _disposed;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public async ValueTask<IProjectExecutionLease?> TryAcquireAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var canonicalProjectDirectory = ProjectExecutionDataDirectory.CanonicalProjectDirectory(
            projectDirectory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leases.TryGetValue(canonicalProjectDirectory, out var existing))
            {
                existing.ReferenceCount = checked(existing.ReferenceCount + 1);
                return new LeaseHandle(this, canonicalProjectDirectory);
            }

            var dataDirectory = ProjectExecutionDataDirectory.ForProjectDirectory(
                canonicalProjectDirectory);
            Directory.CreateDirectory(dataDirectory);
            FileStream stream;
            try
            {
                stream = new FileStream(
                    Path.Combine(dataDirectory, "execution.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            _leases.Add(canonicalProjectDirectory, new LeaseState(stream));
            return new LeaseHandle(this, canonicalProjectDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            foreach (var state in _leases.Values)
            {
                await state.Stream.DisposeAsync().ConfigureAwait(false);
            }

            _leases.Clear();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask ReleaseAsync(string canonicalProjectDirectory)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !_leases.TryGetValue(canonicalProjectDirectory, out var state))
            {
                return;
            }

            state.ReferenceCount--;
            if (state.ReferenceCount == 0)
            {
                _leases.Remove(canonicalProjectDirectory);
                await state.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class LeaseState(FileStream stream)
    {
        public FileStream Stream { get; } = stream;

        public int ReferenceCount { get; set; } = 1;
    }

    private sealed class LeaseHandle(
        ProjectExecutionCoordinator owner,
        string canonicalProjectDirectory) : IProjectExecutionLease
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref _disposed, 1) == 0
                ? owner.ReleaseAsync(canonicalProjectDirectory)
                : ValueTask.CompletedTask;
        }
    }
}

public static class ProjectExecutionDataDirectory
{
    public static string FromProjectTarget(string projectTarget, string currentDirectory)
    {
        return ForProjectDirectory(ProjectDirectoryFromTarget(projectTarget, currentDirectory));
    }

    public static string ProjectDirectoryFromTarget(string projectTarget, string currentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        var targetPath = Path.GetFullPath(
            Path.IsPathRooted(projectTarget)
                ? projectTarget
                : Path.Combine(currentDirectory, projectTarget));
        return targetPath.EndsWith(
            AutomationProjectFileConvention.ProjectFileExtension,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
            ? Path.GetDirectoryName(targetPath)
                ?? throw new InvalidDataException("Project file path has no parent directory.")
            : targetPath;
    }

    public static string ForProjectDirectory(string projectDirectory)
    {
        var canonicalDirectory = CanonicalProjectDirectory(projectDirectory);
        var identity = OperatingSystem.IsWindows()
            ? canonicalDirectory.ToUpperInvariant()
            : canonicalDirectory;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenLineOps",
            "Projects",
            digest);
    }

    internal static string CanonicalProjectDirectory(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (char.IsWhiteSpace(projectDirectory[0]) || char.IsWhiteSpace(projectDirectory[^1]))
        {
            throw new ArgumentException(
                "Project directory must be a canonical path without boundary whitespace.",
                nameof(projectDirectory));
        }

        var fullPath = Path.GetFullPath(projectDirectory);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, PathComparison)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
