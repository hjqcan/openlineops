using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.WorkOrders;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.WorkOrders;
using OpenLineOps.Integration.Infrastructure.Persistence;

namespace OpenLineOps.Integration.Tests;

public sealed class WorkOrderServiceTests
{
    [Fact]
    public async Task ExactCreateReplayIsStableAndChangedCommandContentConflicts()
    {
        using var database = new TemporaryIntegrationDatabase();
        using var store = new SqliteIntegrationStore(database.ConnectionString);
        var service = new WorkOrderService(store);
        var command = new CreateWorkOrderCommand(
            new WorkOrderId("order-create-replay"),
            "product-a",
            5,
            new WorkOrderFactId("fact-create-replay"),
            IntegrationTestData.Epoch,
            "mes-planner");

        var first = await service.CreateAsync(command);
        var replay = await service.CreateAsync(command);

        Assert.Equal(first.Facts, replay.Facts);
        Assert.Single(replay.Facts);
        await Assert.ThrowsAsync<IntegrationMessageConflictException>(
            async () => await service.CreateAsync(
                command with { TargetQuantity = 6 }));
        Assert.Single(
            Assert.IsType<WorkOrder>(await service.GetAsync(command.WorkOrderId))
                .Facts);
    }

    [Fact]
    public async Task ExactTransitionReplayIsStableAndChangedContentConflicts()
    {
        using var database = new TemporaryIntegrationDatabase();
        using var store = new SqliteIntegrationStore(database.ConnectionString);
        var service = new WorkOrderService(store);
        var orderId = new WorkOrderId("order-service-replay");
        await service.CreateAsync(new CreateWorkOrderCommand(
            orderId,
            "product-a",
            5,
            new WorkOrderFactId("fact-service-create"),
            IntegrationTestData.Epoch,
            "planner"));
        var transition = new TransitionWorkOrderCommand(
            orderId,
            new WorkOrderFactId("fact-service-release"),
            WorkOrderFactKind.Released,
            IntegrationTestData.Epoch.AddMinutes(1),
            "operator-a",
            Reason: null);

        var first = await service.TransitionAsync(transition);
        var replay = await service.TransitionAsync(transition);

        Assert.Equal(first.Facts, replay.Facts);
        Assert.Equal(2, replay.Facts.Count);
        var conflict = transition with { ActorId = "operator-b" };
        await Assert.ThrowsAsync<IntegrationMessageConflictException>(
            async () => await service.TransitionAsync(conflict));
        Assert.Equal(2, (await service.GetAsync(orderId))!.Facts.Count);
    }
}
