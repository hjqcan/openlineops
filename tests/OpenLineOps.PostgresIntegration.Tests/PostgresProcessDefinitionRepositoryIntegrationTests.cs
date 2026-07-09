using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Operations;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.PostgresIntegration.Tests;

[Collection(PostgresContainerGroup.Name)]
public sealed class PostgresProcessDefinitionRepositoryIntegrationTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(5);

    private readonly PostgresContainerFixture _postgres;

    public PostgresProcessDefinitionRepositoryIntegrationTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [PostgresIntegrationFact]
    public async Task SaveAsyncPersistsPublishedDefinitionForNewRepositoryInstance()
    {
        var definitionId = $"process-postgres-{Guid.NewGuid():N}";
        var definition = CreateValidDefinition(definitionId);

        AssertAccepted(definition.Publish(PublishedAtUtc));

        await using (var repository = new PostgresProcessDefinitionRepository(_postgres.ConnectionString))
        {
            await repository.SaveAsync(definition);
        }

        await using var restartedRepository = new PostgresProcessDefinitionRepository(_postgres.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(definition.Id);
        var definitions = await restartedRepository.ListAsync();

        Assert.NotNull(restored);
        Assert.Equal(definition.Id, restored.Id);
        Assert.Equal(definition.VersionId, restored.VersionId);
        Assert.Equal(ProcessDefinitionStatus.Published, restored.Status);
        Assert.Equal(PublishedAtUtc, restored.PublishedAtUtc);
        Assert.Empty(restored.DomainEvents);
        Assert.Contains(definitions, persisted => persisted.Id == definition.Id);

        var commandNode = Assert.Single(restored.Nodes, node => node.Kind == ProcessNodeKind.Command);
        Assert.Equal("Inspect", commandNode.CommandName);
        Assert.Equal(TimeSpan.FromSeconds(30), commandNode.CommandTimeout);
        Assert.Equal("scan-ok", commandNode.InputPayload);
        Assert.Equal(new ProcessCapabilityId("vision-camera"), commandNode.RequiredCapability);
    }

    private static ProcessDefinition CreateValidDefinition(string definitionId)
    {
        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId(definitionId),
            new ProcessVersionId($"{definitionId}@1.0.0"),
            "Packaging Line End Of Line Test",
            CreatedAtUtc);

        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            CapabilityId("vision-camera"),
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromSeconds(30),
            inputPayload: "scan-ok"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("start-to-inspect"),
            NodeId("start"),
            NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("inspect-to-end"),
            NodeId("inspect"),
            NodeId("end")));

        return definition;
    }

    private static void AddNode(ProcessDefinition definition, ProcessNode node)
    {
        AssertAccepted(definition.AddNode(node));
    }

    private static void AddTransition(ProcessDefinition definition, ProcessTransition transition)
    {
        AssertAccepted(definition.AddTransition(transition));
    }

    private static void AssertAccepted(ProcessOperationResult result)
    {
        Assert.True(result.Succeeded, result.Message);
    }

    private static ProcessNodeId NodeId(string value)
    {
        return new ProcessNodeId(value);
    }

    private static ProcessTransitionId TransitionId(string value)
    {
        return new ProcessTransitionId(value);
    }

    private static ProcessCapabilityId CapabilityId(string value)
    {
        return new ProcessCapabilityId(value);
    }
}
