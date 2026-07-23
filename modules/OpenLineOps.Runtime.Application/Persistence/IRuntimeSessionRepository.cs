using OpenLineOps.Domain.Abstractions.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IRuntimeSessionRepository
{
    ValueTask SaveAsync(
        RuntimeSession session,
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default);

    ValueTask<RuntimeSession?> GetByIdAsync(RuntimeSessionId sessionId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<RuntimeSession>> ListRecoverableAsync(CancellationToken cancellationToken = default);
}
