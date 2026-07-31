using System.Collections.Concurrent;

namespace OpenLineOps.Application.Abstractions.ProjectWorkspaces;

public sealed class ProjectWorkspaceWriteLockPool
{
    private readonly ConcurrentDictionary<string, Entry> _entries;

    public ProjectWorkspaceWriteLockPool()
    {
        _entries = new ConcurrentDictionary<string, Entry>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
    }

    public int RetainedKeyCount => _entries.Count;

    public async ValueTask<IDisposable> AcquireAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var canonicalKey = Path.GetFullPath(key);

        while (true)
        {
            var entry = _entries.GetOrAdd(canonicalKey, static _ => new Entry());
            if (!entry.TryAddReference())
            {
                RemoveRetiredEntry(canonicalKey, entry);
                continue;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new Lease(this, canonicalKey, entry);
            }
            catch
            {
                ReleaseReference(canonicalKey, entry);
                throw;
            }
        }
    }

    private void ReleaseLease(string key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(key, entry);
    }

    private void ReleaseReference(string key, Entry entry)
    {
        if (entry.ReleaseReferenceAndTryRetire())
        {
            RemoveRetiredEntry(key, entry);
        }
    }

    private void RemoveRetiredEntry(string key, Entry entry)
    {
        if (_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
        {
            entry.Dispose();
        }
    }

    private sealed class Entry : IDisposable
    {
        private const int Retired = -1;
        private int _referenceState;
        private int _disposed;

        public SemaphoreSlim Semaphore { get; } = new(initialCount: 1, maxCount: 1);

        public bool TryAddReference()
        {
            while (true)
            {
                var current = Volatile.Read(ref _referenceState);
                if (current == Retired)
                {
                    return false;
                }

                if (current == int.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Project workspace write-lock reference capacity was exceeded.");
                }

                if (Interlocked.CompareExchange(
                        ref _referenceState,
                        current + 1,
                        current) == current)
                {
                    return true;
                }
            }
        }

        public bool ReleaseReferenceAndTryRetire()
        {
            while (true)
            {
                var current = Volatile.Read(ref _referenceState);
                if (current <= 0)
                {
                    throw new InvalidOperationException(
                        "Project workspace write-lock reference was released more than once.");
                }

                var next = current == 1 ? Retired : current - 1;
                if (Interlocked.CompareExchange(ref _referenceState, next, current) == current)
                {
                    return next == Retired;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Semaphore.Dispose();
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private ProjectWorkspaceWriteLockPool? _owner;
        private readonly string _key;
        private readonly Entry _entry;

        public Lease(ProjectWorkspaceWriteLockPool owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseLease(_key, _entry);
        }
    }
}
