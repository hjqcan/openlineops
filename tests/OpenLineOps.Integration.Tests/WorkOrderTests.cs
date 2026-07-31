using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.WorkOrders;

namespace OpenLineOps.Integration.Tests;

public sealed class WorkOrderTests
{
    [Fact]
    public void LifecycleIsDerivedFromAnAppendOnlyFactStream()
    {
        var workOrderId = new WorkOrderId("order-001");
        var order = WorkOrder.Create(
            workOrderId,
            "product-a",
            100,
            new WorkOrderFactId("fact-created"),
            IntegrationTestData.Epoch,
            "planner");

        order.Release(
            new WorkOrderFactId("fact-released"),
            IntegrationTestData.Epoch.AddMinutes(1),
            "supervisor");
        order.Hold(
            new WorkOrderFactId("fact-held"),
            IntegrationTestData.Epoch.AddMinutes(2),
            "quality",
            "First article review");
        order.Resume(
            new WorkOrderFactId("fact-resumed"),
            IntegrationTestData.Epoch.AddMinutes(3),
            "quality");
        order.Complete(
            new WorkOrderFactId("fact-completed"),
            IntegrationTestData.Epoch.AddMinutes(4),
            "coordinator");

        Assert.Equal(WorkOrderStatus.Completed, order.Status);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], order.Facts.Select(static fact => fact.Sequence));
        Assert.Equal(
            [
                WorkOrderFactKind.Created,
                WorkOrderFactKind.Released,
                WorkOrderFactKind.Held,
                WorkOrderFactKind.Resumed,
                WorkOrderFactKind.Completed
            ],
            order.Facts.Select(static fact => fact.Kind));

        var rehydrated = WorkOrder.Rehydrate(order.Facts.Reverse());

        Assert.Equal(order.Id, rehydrated.Id);
        Assert.Equal(order.ProductModelId, rehydrated.ProductModelId);
        Assert.Equal(order.TargetQuantity, rehydrated.TargetQuantity);
        Assert.Equal(order.Status, rehydrated.Status);
        Assert.Equal(order.Facts, rehydrated.Facts);
    }

    [Fact]
    public void InvalidTransitionAndMissingHoldReasonAreRejected()
    {
        var order = WorkOrder.Create(
            new WorkOrderId("order-002"),
            "product-b",
            1,
            new WorkOrderFactId("fact-created-002"),
            IntegrationTestData.Epoch,
            "planner");

        Assert.Throws<InvalidOperationException>(() => order.Complete(
            new WorkOrderFactId("fact-completed-002"),
            IntegrationTestData.Epoch.AddMinutes(1),
            "coordinator"));

        Assert.Throws<ArgumentException>(() => new WorkOrderFact(
            new WorkOrderFactId("fact-held-002"),
            order.Id,
            2,
            WorkOrderFactKind.Held,
            WorkOrderStatus.Held,
            IntegrationTestData.Epoch.AddMinutes(1),
            "quality"));

        Assert.Throws<ArgumentException>(() => new WorkOrderFact(
            new WorkOrderFactId("fact-invalid-result-002"),
            order.Id,
            2,
            WorkOrderFactKind.Released,
            WorkOrderStatus.Completed,
            IntegrationTestData.Epoch.AddMinutes(1),
            "planner"));

        var reusedFactId = new WorkOrderFact(
            order.Facts[0].Id,
            order.Id,
            2,
            WorkOrderFactKind.Released,
            WorkOrderStatus.Released,
            IntegrationTestData.Epoch.AddMinutes(1),
            "planner");
        Assert.Throws<InvalidOperationException>(
            () => WorkOrder.Rehydrate([order.Facts[0], reusedFactId]));
    }
}
