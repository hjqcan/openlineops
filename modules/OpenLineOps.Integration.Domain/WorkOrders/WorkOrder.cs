using System.Collections.ObjectModel;
using OpenLineOps.Integration.Domain.Identifiers;

namespace OpenLineOps.Integration.Domain.WorkOrders;

public enum WorkOrderStatus
{
    Draft = 0,
    Released = 1,
    Held = 2,
    Completed = 3,
    Cancelled = 4
}

public enum WorkOrderFactKind
{
    Created = 0,
    Released = 1,
    Held = 2,
    Resumed = 3,
    Completed = 4,
    Cancelled = 5
}

public sealed record WorkOrderFact
{
    public WorkOrderFact(
        WorkOrderFactId id,
        WorkOrderId workOrderId,
        long sequence,
        WorkOrderFactKind kind,
        WorkOrderStatus resultingStatus,
        DateTimeOffset occurredAtUtc,
        string actorId,
        string? productModelId = null,
        int? targetQuantity = null,
        string? reason = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        WorkOrderId = workOrderId ?? throw new ArgumentNullException(nameof(workOrderId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        Sequence = sequence;
        Kind = IntegrationGuard.Defined(kind, nameof(kind));
        ResultingStatus = IntegrationGuard.Defined(resultingStatus, nameof(resultingStatus));
        OccurredAtUtc = IntegrationGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        ActorId = IntegrationGuard.RequiredCanonical(actorId, nameof(actorId));
        ProductModelId = IntegrationGuard.OptionalCanonical(productModelId, nameof(productModelId));
        if (targetQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetQuantity),
                "Target quantity must be positive when provided.");
        }

        TargetQuantity = targetQuantity;
        Reason = IntegrationGuard.OptionalCanonical(reason, nameof(reason));
        ValidateShape();
    }

    public WorkOrderFactId Id { get; }

    public WorkOrderId WorkOrderId { get; }

    public long Sequence { get; }

    public WorkOrderFactKind Kind { get; }

    public WorkOrderStatus ResultingStatus { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string ActorId { get; }

    public string? ProductModelId { get; }

    public int? TargetQuantity { get; }

    public string? Reason { get; }

    private void ValidateShape()
    {
        if (Kind == WorkOrderFactKind.Created)
        {
            if (Sequence != 1
                || ResultingStatus != WorkOrderStatus.Draft
                || ProductModelId is null
                || TargetQuantity is null
                || Reason is not null)
            {
                throw new ArgumentException(
                    "A Created fact must be sequence one with product, quantity, and Draft status.");
            }

            return;
        }

        if (ProductModelId is not null || TargetQuantity is not null)
        {
            throw new ArgumentException(
                "Only a Created fact can declare product model and target quantity.");
        }

        var expectedStatus = Kind switch
        {
            WorkOrderFactKind.Released or WorkOrderFactKind.Resumed =>
                WorkOrderStatus.Released,
            WorkOrderFactKind.Held => WorkOrderStatus.Held,
            WorkOrderFactKind.Completed => WorkOrderStatus.Completed,
            WorkOrderFactKind.Cancelled => WorkOrderStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(Kind))
        };
        if (ResultingStatus != expectedStatus)
        {
            throw new ArgumentException(
                $"A {Kind} fact must result in {expectedStatus} status.",
                nameof(ResultingStatus));
        }

        if (Kind is WorkOrderFactKind.Held or WorkOrderFactKind.Cancelled)
        {
            if (Reason is null)
            {
                throw new ArgumentException(
                    "Held and Cancelled facts require a reason.",
                    nameof(Reason));
            }
        }
        else if (Reason is not null)
        {
            throw new ArgumentException(
                "Only Held and Cancelled facts can declare a reason.",
                nameof(Reason));
        }
    }
}

public sealed class WorkOrder
{
    private readonly List<WorkOrderFact> _facts = [];

    private WorkOrder()
    {
    }

    public WorkOrderId Id { get; private set; } = null!;

    public string ProductModelId { get; private set; } = null!;

    public int TargetQuantity { get; private set; }

    public WorkOrderStatus Status { get; private set; }

    public IReadOnlyList<WorkOrderFact> Facts =>
        new ReadOnlyCollection<WorkOrderFact>(_facts);

    public static WorkOrder Create(
        WorkOrderId id,
        string productModelId,
        int targetQuantity,
        WorkOrderFactId factId,
        DateTimeOffset occurredAtUtc,
        string actorId)
    {
        var order = new WorkOrder();
        order.Apply(new WorkOrderFact(
            factId,
            id,
            1,
            WorkOrderFactKind.Created,
            WorkOrderStatus.Draft,
            occurredAtUtc,
            actorId,
            productModelId,
            targetQuantity));
        return order;
    }

    public static WorkOrder Rehydrate(IEnumerable<WorkOrderFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var snapshot = facts.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("A work order requires at least one fact.", nameof(facts));
        }

        var order = new WorkOrder();
        foreach (var fact in snapshot.OrderBy(static fact => fact.Sequence))
        {
            order.Apply(fact);
        }

        return order;
    }

    public WorkOrderFact Release(
        WorkOrderFactId factId,
        DateTimeOffset occurredAtUtc,
        string actorId) =>
        Append(factId, WorkOrderFactKind.Released, WorkOrderStatus.Released, occurredAtUtc, actorId);

    public WorkOrderFact Hold(
        WorkOrderFactId factId,
        DateTimeOffset occurredAtUtc,
        string actorId,
        string reason) =>
        Append(
            factId,
            WorkOrderFactKind.Held,
            WorkOrderStatus.Held,
            occurredAtUtc,
            actorId,
            reason);

    public WorkOrderFact Resume(
        WorkOrderFactId factId,
        DateTimeOffset occurredAtUtc,
        string actorId) =>
        Append(factId, WorkOrderFactKind.Resumed, WorkOrderStatus.Released, occurredAtUtc, actorId);

    public WorkOrderFact Complete(
        WorkOrderFactId factId,
        DateTimeOffset occurredAtUtc,
        string actorId) =>
        Append(factId, WorkOrderFactKind.Completed, WorkOrderStatus.Completed, occurredAtUtc, actorId);

    public WorkOrderFact Cancel(
        WorkOrderFactId factId,
        DateTimeOffset occurredAtUtc,
        string actorId,
        string reason) =>
        Append(
            factId,
            WorkOrderFactKind.Cancelled,
            WorkOrderStatus.Cancelled,
            occurredAtUtc,
            actorId,
            reason);

    private WorkOrderFact Append(
        WorkOrderFactId factId,
        WorkOrderFactKind kind,
        WorkOrderStatus resultingStatus,
        DateTimeOffset occurredAtUtc,
        string actorId,
        string? reason = null)
    {
        var fact = new WorkOrderFact(
            factId,
            Id,
            _facts.Count + 1,
            kind,
            resultingStatus,
            occurredAtUtc,
            actorId,
            reason: reason);
        Apply(fact);
        return fact;
    }

    private void Apply(WorkOrderFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (_facts.Any(existing => existing.Id == fact.Id))
        {
            throw new InvalidOperationException(
                $"Work order fact id '{fact.Id.Value}' cannot be appended more than once.");
        }

        if (_facts.Count == 0)
        {
            if (fact.Kind != WorkOrderFactKind.Created)
            {
                throw new InvalidOperationException("The first work order fact must be Created.");
            }

            Id = fact.WorkOrderId;
            ProductModelId = fact.ProductModelId!;
            TargetQuantity = fact.TargetQuantity!.Value;
        }
        else
        {
            if (fact.WorkOrderId != Id || fact.Sequence != _facts.Count + 1)
            {
                throw new InvalidOperationException(
                    "Work order facts must have one identity and contiguous sequence.");
            }

            RequireTransition(Status, fact.Kind, fact.ResultingStatus);
        }

        _facts.Add(fact);
        Status = fact.ResultingStatus;
    }

    private static void RequireTransition(
        WorkOrderStatus current,
        WorkOrderFactKind kind,
        WorkOrderStatus resulting)
    {
        var valid = (current, kind, resulting) switch
        {
            (WorkOrderStatus.Draft, WorkOrderFactKind.Released, WorkOrderStatus.Released) => true,
            (WorkOrderStatus.Released, WorkOrderFactKind.Held, WorkOrderStatus.Held) => true,
            (WorkOrderStatus.Held, WorkOrderFactKind.Resumed, WorkOrderStatus.Released) => true,
            (WorkOrderStatus.Released, WorkOrderFactKind.Completed, WorkOrderStatus.Completed) => true,
            (WorkOrderStatus.Draft or WorkOrderStatus.Released or WorkOrderStatus.Held,
                WorkOrderFactKind.Cancelled,
                WorkOrderStatus.Cancelled) => true,
            _ => false
        };

        if (!valid)
        {
            throw new InvalidOperationException(
                $"Work order transition {current} -> {kind}/{resulting} is not allowed.");
        }
    }
}
