using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Operations;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.Processes.Tests;

public sealed class SqliteProcessDefinitionRepositoryTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(5);

    [Fact]
    public async Task SaveAsyncPersistsPublishedDefinitionForNewRepositoryInstance()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProcessDefinitionRepository(database.ConnectionString);
        var definition = CreateValidDefinition("packaging-line-eol");

        AssertAccepted(definition.Publish(PublishedAtUtc));
        await repository.SaveAsync(definition);

        using var restartedRepository = new SqliteProcessDefinitionRepository(database.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(definition.Id);

        Assert.NotNull(restored);
        Assert.Equal(definition.Id, restored.Id);
        Assert.Equal(definition.VersionId, restored.VersionId);
        Assert.Equal(definition.DisplayName, restored.DisplayName);
        Assert.Equal(ProcessDefinitionStatus.Published, restored.Status);
        Assert.Equal(CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(PublishedAtUtc, restored.PublishedAtUtc);
        Assert.Empty(restored.DomainEvents);

        var commandNode = Assert.Single(restored.Nodes, node => node.Kind == ProcessNodeKind.Command);
        Assert.Equal("Inspect", commandNode.CommandName);
        Assert.Equal(TimeSpan.FromSeconds(30), commandNode.CommandTimeout);
        Assert.Equal("scan-ok", commandNode.InputPayload);
        Assert.Equal(new ProcessCapabilityId("vision-camera"), commandNode.RequiredCapability);
        Assert.Equal(2, restored.Transitions.Count);
    }

    [Fact]
    public async Task ListAsyncReturnsPersistedDefinitionsOrderedByDefinitionId()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProcessDefinitionRepository(database.ConnectionString);

        await repository.SaveAsync(CreateValidDefinition("z-process"));
        await repository.SaveAsync(CreateValidDefinition("a-process"));

        var definitions = await repository.ListAsync();

        Assert.Collection(
            definitions,
            definition => Assert.Equal(new ProcessDefinitionId("a-process"), definition.Id),
            definition => Assert.Equal(new ProcessDefinitionId("z-process"), definition.Id));
    }

    [Fact]
    public async Task SaveAsyncPersistsPythonScriptNodeMetadata()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProcessDefinitionRepository(database.ConnectionString);
        var definition = CreatePythonScriptDefinition("python-script-process");

        await repository.SaveAsync(definition);

        using var restartedRepository = new SqliteProcessDefinitionRepository(database.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(definition.Id);

        Assert.NotNull(restored);
        var scriptNode = Assert.Single(restored.Nodes, node => node.Kind == ProcessNodeKind.PythonScript);
        Assert.Equal("Python", scriptNode.ScriptLanguage);
        Assert.Null(scriptNode.BlocklyWorkspaceJson);
        Assert.Equal("result = {'normalized': input_payload}", scriptNode.ScriptSourceCode);
        Assert.False(string.IsNullOrWhiteSpace(scriptNode.ScriptSourceHash));
        Assert.Equal("2", scriptNode.ScriptVersion);
        Assert.Equal(TimeSpan.FromSeconds(12), scriptNode.ScriptTimeout);
        Assert.Equal("scan-ok", scriptNode.InputPayload);
    }

    [Fact]
    public async Task SaveAsyncPersistsTransitionLoopPolicy()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProcessDefinitionRepository(database.ConnectionString);
        var definition = CreateLoopingDefinition("looping-process");

        AssertAccepted(definition.Publish(PublishedAtUtc));
        await repository.SaveAsync(definition);

        using var restartedRepository = new SqliteProcessDefinitionRepository(database.ConnectionString);
        var restored = await restartedRepository.GetByIdAsync(definition.Id);

        Assert.NotNull(restored);
        var loopTransition = Assert.Single(
            restored.Transitions,
            transition => transition.Id == TransitionId("route-to-inspect-retry"));
        Assert.Equal(ProcessTransitionLoopPolicy.Counted, loopTransition.LoopPolicy);
        Assert.Equal(3, loopTransition.MaxTraversals);
    }

    [Fact]
    public async Task GetByIdAsyncReturnsNullForMissingDefinition()
    {
        using var database = TemporarySqliteDatabase.Create();
        using var repository = new SqliteProcessDefinitionRepository(database.ConnectionString);

        var definition = await repository.GetByIdAsync(new ProcessDefinitionId("missing-process"));

        Assert.Null(definition);
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

    private static ProcessDefinition CreateLoopingDefinition(string definitionId)
    {
        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId(definitionId),
            new ProcessVersionId($"{definitionId}@1.0.0"),
            "Looping Process",
            CreatedAtUtc);

        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.Command(
            NodeId("inspect"),
            "Inspect",
            CapabilityId("vision-camera"),
            commandName: "Inspect",
            commandTimeout: TimeSpan.FromSeconds(30),
            inputPayload: "scan-ok"));
        AddNode(definition, ProcessNode.Decision(NodeId("route"), "Route Result"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("start-to-inspect"),
            NodeId("start"),
            NodeId("inspect")));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("inspect-to-route"),
            NodeId("inspect"),
            NodeId("route")));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("route-to-inspect-retry"),
            NodeId("route"),
            NodeId("inspect"),
            label: "retry",
            loopPolicy: ProcessTransitionLoopPolicy.Counted,
            maxTraversals: 3));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("route-to-end-ok"),
            NodeId("route"),
            NodeId("end"),
            label: "ok"));

        return definition;
    }

    private static ProcessDefinition CreatePythonScriptDefinition(string definitionId)
    {
        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId(definitionId),
            new ProcessVersionId($"{definitionId}@1.0.0"),
            "Python Script Process",
            CreatedAtUtc);

        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.PythonScript(
            NodeId("normalize"),
            "Normalize Measurement",
            sourceCode: "result = {'normalized': input_payload}",
            scriptVersion: "2",
            scriptTimeout: TimeSpan.FromSeconds(12),
            inputPayload: "scan-ok"));
        AddNode(definition, ProcessNode.End(NodeId("end"), "End"));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("start-to-normalize"),
            NodeId("start"),
            NodeId("normalize")));
        AddTransition(definition, ProcessTransition.Create(
            TransitionId("normalize-to-end"),
            NodeId("normalize"),
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

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        private TemporarySqliteDatabase(string directory, string databasePath)
        {
            Directory = directory;
            ConnectionString = $"Data Source={databasePath};Pooling=False";
        }

        public string Directory { get; }

        public string ConnectionString { get; }

        public static TemporarySqliteDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "OpenLineOps", Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(directory, "process-definitions.sqlite");

            return new TemporarySqliteDatabase(directory, databasePath);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
