using System.Collections.Concurrent;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryRuntimeSessionRepository : IRuntimeSessionRepository
{
    private readonly ConcurrentDictionary<RuntimeSessionId, RuntimeSession> _sessions = [];
    private int _saveCount;

    public int SaveCount => Volatile.Read(ref _saveCount);

    public ValueTask SaveAsync(RuntimeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        _sessions[session.Id] = session;
        Interlocked.Increment(ref _saveCount);

        return ValueTask.CompletedTask;
    }

    public ValueTask<RuntimeSession?> GetByIdAsync(
        RuntimeSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _sessions.TryGetValue(sessionId, out var session);

        return ValueTask.FromResult(session);
    }

    public ValueTask<IReadOnlyCollection<RuntimeSession>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessions = _sessions.Values
            .Where(session => !session.IsTerminal)
            .OrderBy(session => session.LastTransitionAtUtc)
            .ThenBy(session => session.Id.Value)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyCollection<RuntimeSession>>(sessions);
    }
}
