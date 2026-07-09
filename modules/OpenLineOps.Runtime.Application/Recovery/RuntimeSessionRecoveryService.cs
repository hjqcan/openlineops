using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Sessions;

namespace OpenLineOps.Runtime.Application.Recovery;

public sealed class RuntimeSessionRecoveryService : IRuntimeSessionRecoveryService
{
    private readonly IRuntimeSessionRepository _sessionRepository;

    public RuntimeSessionRecoveryService(IRuntimeSessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    public async ValueTask<RuntimeSessionRecoveryPlan> CreateRecoveryPlanAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = await _sessionRepository
            .ListRecoverableAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = sessions
            .Where(session => !session.IsTerminal)
            .OrderBy(session => session.LastTransitionAtUtc)
            .ThenBy(session => session.Id.Value)
            .Select(session => new RuntimeSessionRecoveryCandidate(
                session.Id,
                session.StationId,
                session.ProcessVersionId,
                session.ConfigurationSnapshotId,
                session.RecipeSnapshotId,
                session.Status,
                session.LastTransitionAtUtc,
                ResolveRecoveryReason(session.Status)))
            .ToArray();

        return new RuntimeSessionRecoveryPlan(candidates);
    }

    private static string ResolveRecoveryReason(RuntimeSessionStatus status)
    {
        return status switch
        {
            RuntimeSessionStatus.Created => "Session was created but never started.",
            RuntimeSessionStatus.Queued => "Session was queued before shutdown.",
            RuntimeSessionStatus.Running => "Session was running during shutdown.",
            RuntimeSessionStatus.Pausing => "Session was pausing during shutdown.",
            RuntimeSessionStatus.Paused => "Session was paused during shutdown.",
            RuntimeSessionStatus.Stopping => "Session was stopping during shutdown.",
            _ => "Session requires operator review."
        };
    }
}
