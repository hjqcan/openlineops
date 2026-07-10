using Microsoft.Data.Sqlite;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class SqliteRuntimeStoreExclusiveLease : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _lockFilePath;
    private FileStream? _handle;
    private bool _disposed;

    public SqliteRuntimeStoreExclusiveLease(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)
            || char.IsWhiteSpace(connectionString[0])
            || char.IsWhiteSpace(connectionString[^1]))
        {
            throw new ArgumentException(
                "Runtime SQLite connection string must be a non-empty canonical string.",
                nameof(connectionString));
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (builder.Mode == SqliteOpenMode.Memory
            || string.IsNullOrWhiteSpace(dataSource)
            || char.IsWhiteSpace(dataSource[0])
            || char.IsWhiteSpace(dataSource[^1])
            || dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Runtime SQLite host ownership requires a file-backed database declared by a canonical filesystem path.",
                nameof(connectionString));
        }

        DatabasePath = Path.GetFullPath(dataSource);
        _lockFilePath = DatabasePath + ".host.lock";
    }

    public string DatabasePath { get; }

    public string LockFilePath => _lockFilePath;

    public bool IsAcquired
    {
        get
        {
            lock (_gate)
            {
                return _handle is not null;
            }
        }
    }

    public ValueTask AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle is not null)
            {
                throw new InvalidOperationException(
                    $"Runtime SQLite store '{DatabasePath}' is already owned by this host.");
            }

            var directory = Path.GetDirectoryName(DatabasePath)
                ?? throw new InvalidOperationException(
                    $"Runtime SQLite store '{DatabasePath}' has no parent directory.");
            Directory.CreateDirectory(directory);
            try
            {
                _handle = new FileStream(
                    _lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Runtime SQLite store '{DatabasePath}' is already owned by another host or cannot be locked.",
                    exception);
            }
        }

        return ValueTask.CompletedTask;
    }

    public void EnsureAcquired()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle is null)
            {
                throw new InvalidOperationException(
                    $"Runtime SQLite store '{DatabasePath}' must be exclusively owned before startup recovery.");
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _handle?.Dispose();
            _handle = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _handle?.Dispose();
            _handle = null;
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
