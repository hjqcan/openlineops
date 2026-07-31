using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.WorkOrders;

namespace OpenLineOps.Integration.Application.WorkOrders;

public sealed record CreateWorkOrderCommand(
    WorkOrderId WorkOrderId,
    string ProductModelId,
    int TargetQuantity,
    WorkOrderFactId FactId,
    DateTimeOffset OccurredAtUtc,
    string ActorId);

public sealed record TransitionWorkOrderCommand(
    WorkOrderId WorkOrderId,
    WorkOrderFactId FactId,
    WorkOrderFactKind Kind,
    DateTimeOffset OccurredAtUtc,
    string ActorId,
    string? Reason);

public sealed class WorkOrderService(IWorkOrderFactStore store)
{
    public async ValueTask<WorkOrder> CreateAsync(
        CreateWorkOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var order = WorkOrder.Create(
            command.WorkOrderId,
            command.ProductModelId,
            command.TargetQuantity,
            command.FactId,
            command.OccurredAtUtc,
            command.ActorId);
        await store.AppendAsync(order.Facts, cancellationToken).ConfigureAwait(false);
        return await GetRequiredAsync(command.WorkOrderId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkOrder?> GetAsync(
        WorkOrderId workOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workOrderId);
        var facts = await store.ListAsync(workOrderId, cancellationToken).ConfigureAwait(false);
        return facts.Count == 0 ? null : WorkOrder.Rehydrate(facts);
    }

    public async ValueTask<WorkOrder> TransitionAsync(
        TransitionWorkOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var order = await GetRequiredAsync(command.WorkOrderId, cancellationToken)
            .ConfigureAwait(false);
        var replayedFact = order.Facts.SingleOrDefault(
            fact => fact.Id == command.FactId);
        if (replayedFact is not null)
        {
            if (Matches(replayedFact, command))
            {
                return order;
            }

            throw new IntegrationMessageConflictException(
                $"Work order fact id '{command.FactId.Value}' was reused with different content.");
        }

        var fact = command.Kind switch
        {
            WorkOrderFactKind.Released => order.Release(
                command.FactId,
                command.OccurredAtUtc,
                command.ActorId),
            WorkOrderFactKind.Held => order.Hold(
                command.FactId,
                command.OccurredAtUtc,
                command.ActorId,
                command.Reason
                ?? throw new ArgumentException(
                    "A Hold transition requires a reason.",
                    nameof(command))),
            WorkOrderFactKind.Resumed => order.Resume(
                command.FactId,
                command.OccurredAtUtc,
                command.ActorId),
            WorkOrderFactKind.Completed => order.Complete(
                command.FactId,
                command.OccurredAtUtc,
                command.ActorId),
            WorkOrderFactKind.Cancelled => order.Cancel(
                command.FactId,
                command.OccurredAtUtc,
                command.ActorId,
                command.Reason
                ?? throw new ArgumentException(
                    "A Cancel transition requires a reason.",
                    nameof(command))),
            _ => throw new ArgumentException(
                $"Work order transition '{command.Kind}' is not supported.",
                nameof(command))
        };
        await store.AppendAsync([fact], cancellationToken).ConfigureAwait(false);
        return await GetRequiredAsync(command.WorkOrderId, cancellationToken).ConfigureAwait(false);
    }

    private static bool Matches(
        WorkOrderFact fact,
        TransitionWorkOrderCommand command)
    {
        return fact.WorkOrderId == command.WorkOrderId
            && fact.Kind == command.Kind
            && fact.ResultingStatus == ResultingStatus(command.Kind)
            && fact.OccurredAtUtc == command.OccurredAtUtc
            && string.Equals(fact.ActorId, command.ActorId, StringComparison.Ordinal)
            && string.Equals(fact.Reason, command.Reason, StringComparison.Ordinal)
            && fact.ProductModelId is null
            && fact.TargetQuantity is null;
    }

    private static WorkOrderStatus ResultingStatus(WorkOrderFactKind kind)
    {
        return kind switch
        {
            WorkOrderFactKind.Released or WorkOrderFactKind.Resumed =>
                WorkOrderStatus.Released,
            WorkOrderFactKind.Held => WorkOrderStatus.Held,
            WorkOrderFactKind.Completed => WorkOrderStatus.Completed,
            WorkOrderFactKind.Cancelled => WorkOrderStatus.Cancelled,
            _ => throw new ArgumentException(
                $"Work order transition '{kind}' is not supported.",
                nameof(kind))
        };
    }

    private async ValueTask<WorkOrder> GetRequiredAsync(
        WorkOrderId workOrderId,
        CancellationToken cancellationToken)
    {
        return await GetAsync(workOrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Work order '{workOrderId.Value}' does not exist.");
    }
}
