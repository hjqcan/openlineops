namespace OpenLineOps.Integration.Application.Outbox;

public sealed class IntegrationOutboxReplayService(
    IIntegrationOutboxStore store,
    IIntegrationReplayAuthorizer authorizer)
{
    public async ValueTask ReplayAsync(
        ManualOutboxReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authorizer.IsAuthorizedAsync(request, cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException(
                $"Actor '{request.ActorId}' is not authorized to replay integration messages.");
        }

        await store.RequeueDeadLetterAsync(
                request.MessageId,
                request.ActorId,
                request.Reason,
                request.RequestedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
