using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Operations;
using OpenLineOps.Processes.Domain.Transitions;

namespace OpenLineOps.Processes.Tests;

public sealed class ProcessDefinitionServiceTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(5);
    private const string ReplacementBlocklyWorkspaceJson =
        """{"blocks":{"languageVersion":0,"blocks":[{"type":"flow_wait","id":"wait-1"}]}}""";

    [Fact]
    public async Task ReplaceDraftAsyncReplacesEntireGraphAndScriptArtifactsAndPreservesCreatedAt()
    {
        var original = CreatePythonScriptDefinition();
        var repository = new FakeProcessDefinitionRepository(original);
        var service = CreateService(repository);

        var result = await service.ReplaceDraftAsync(
            original.Id.Value,
            CreateReplacementRequest(original.Id.Value));

        Assert.True(result.IsSuccess);
        Assert.Equal(original.Id.Value, result.Value.ProcessDefinitionId);
        Assert.Equal("python-script-process@2.0.0", result.Value.VersionId);
        Assert.Equal("Replacement Python Script Process", result.Value.DisplayName);
        Assert.Equal("Draft", result.Value.Status);
        Assert.Equal(CreatedAtUtc, result.Value.CreatedAtUtc);
        Assert.Null(result.Value.PublishedAtUtc);
        Assert.Equal(3, result.Value.Nodes.Count);
        Assert.DoesNotContain(result.Value.Nodes, node => node.NodeId == "normalize");

        var scriptNode = Assert.Single(result.Value.Nodes, node => node.Kind == "Blockly");
        Assert.Equal("inspect", scriptNode.NodeId);
        Assert.Equal("Inspect With Blockly", scriptNode.DisplayName);
        Assert.Equal(25, scriptNode.TimeoutSeconds);
        Assert.Equal(ReplacementBlocklyWorkspaceJson, scriptNode.BlocklyWorkspaceJson);
        Assert.Null(scriptNode.ScriptSourceCode);
        Assert.Null(scriptNode.ScriptSourceHash);
        Assert.Null(scriptNode.ScriptVersion);
        Assert.Equal("""{"partId":"P-42"}""", scriptNode.InputPayload);
        Assert.Equal(
            ["replacement-inspect-to-end", "replacement-start-to-inspect"],
            result.Value.Transitions
                .Select(transition => transition.TransitionId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(1, repository.SaveCount);

        var stored = await repository.GetByIdAsync(original.Id);
        Assert.NotNull(stored);
        Assert.NotSame(original, stored);
        Assert.Equal(CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal("Replacement Python Script Process", stored.DisplayName);
    }

    [Fact]
    public async Task ReplaceDraftAsyncRejectsPublishedDefinitionWithoutSaving()
    {
        var published = CreatePythonScriptDefinition();
        AssertAccepted(published.Publish(PublishedAtUtc));
        var repository = new FakeProcessDefinitionRepository(published);
        var service = CreateService(repository);

        var result = await service.ReplaceDraftAsync(
            published.Id.Value,
            CreateReplacementRequest(published.Id.Value));

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Processes.DefinitionImmutable", result.Error.Code);
        Assert.Equal(0, repository.SaveCount);
        Assert.Equal(ProcessDefinitionStatus.Published, published.Status);
        Assert.Equal("Python Script Process", published.DisplayName);
        Assert.Contains(published.Nodes, node => node.Id.Value == "normalize");
    }

    [Fact]
    public async Task ReplaceDraftAsyncRejectsRouteAndBodyProcessDefinitionIdMismatch()
    {
        var original = CreatePythonScriptDefinition();
        var repository = new FakeProcessDefinitionRepository(original);
        var service = CreateService(repository);

        var result = await service.ReplaceDraftAsync(
            original.Id.Value,
            CreateReplacementRequest("different-process"));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.DefinitionIdMismatch", result.Error.Code);
        Assert.Equal(0, repository.SaveCount);
        Assert.Equal("Python Script Process", original.DisplayName);
    }

    [Fact]
    public async Task ReplaceDraftAsyncReturnsNotFoundWhenDefinitionDoesNotExist()
    {
        var repository = new FakeProcessDefinitionRepository();
        var service = CreateService(repository);

        var result = await service.ReplaceDraftAsync(
            "missing-process",
            CreateReplacementRequest("missing-process"));

        Assert.True(result.IsFailure);
        Assert.Equal("NotFound.Processes.DefinitionNotFound", result.Error.Code);
        Assert.Equal(0, repository.SaveCount);
    }

    [Fact]
    public async Task PublishAsyncWithPythonScriptValidationIssueDoesNotPublishDefinition()
    {
        var definition = CreatePythonScriptDefinition();
        var repository = new FakeProcessDefinitionRepository(definition);
        var validator = new FakeProcessScriptDefinitionValidator(new ProcessScriptValidationReport([
            new ProcessScriptValidationIssue(
                "SYNTAX",
                "invalid syntax",
                Line: 1,
                Column: 4)
        ]));
        var service = new ProcessDefinitionService(repository, new FixedClock(PublishedAtUtc), validator);

        var result = await service.PublishAsync(definition.Id.Value);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Processes.PythonScriptValidationFailed", result.Error.Code);
        Assert.Equal(ProcessDefinitionStatus.Draft, definition.Status);
        Assert.Null(definition.PublishedAtUtc);
        Assert.Equal(0, repository.SaveCount);
    }

    [Fact]
    public async Task PublishAsyncWithValidPythonScriptPublishesDefinition()
    {
        var definition = CreatePythonScriptDefinition();
        var repository = new FakeProcessDefinitionRepository(definition);
        var service = new ProcessDefinitionService(
            repository,
            new FixedClock(PublishedAtUtc),
            new FakeProcessScriptDefinitionValidator(ProcessScriptValidationReport.Valid));

        var result = await service.PublishAsync(definition.Id.Value);

        Assert.True(result.IsSuccess);
        Assert.Equal(ProcessDefinitionStatus.Published, definition.Status);
        Assert.Equal(PublishedAtUtc, definition.PublishedAtUtc);
        Assert.Equal(1, repository.SaveCount);
    }

    private static ProcessDefinition CreatePythonScriptDefinition()
    {
        var definition = ProcessDefinition.Create(
            new ProcessDefinitionId("python-script-process"),
            new ProcessVersionId("python-script-process@1.0.0"),
            "Python Script Process",
            CreatedAtUtc);

        AddNode(definition, ProcessNode.Start(NodeId("start"), "Start"));
        AddNode(definition, ProcessNode.PythonScript(
            NodeId("normalize"),
            "Normalize Measurement",
            sourceCode: "result = {'normalized': input_payload}",
            scriptVersion: "1",
            scriptTimeout: TimeSpan.FromSeconds(10)));
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

    private static CreateProcessDefinitionRequest CreateReplacementRequest(string processDefinitionId)
    {
        return new CreateProcessDefinitionRequest(
            processDefinitionId,
            "python-script-process@2.0.0",
            "Replacement Python Script Process",
            [
                new CreateProcessNodeRequest(
                    "replacement-start",
                    "Start",
                    "Replacement Start",
                    RequiredCapability: null,
                    CommandName: null,
                    TimeoutSeconds: null,
                    InputPayload: null,
                    BlocklyWorkspaceJson: null,
                    ScriptSourceCode: null,
                    ScriptVersion: null),
                new CreateProcessNodeRequest(
                    "inspect",
                    "Blockly",
                    "Inspect With Blockly",
                    RequiredCapability: null,
                    CommandName: null,
                    TimeoutSeconds: 25,
                    InputPayload: """{"partId":"P-42"}""",
                    BlocklyWorkspaceJson: ReplacementBlocklyWorkspaceJson,
                    ScriptSourceCode: null,
                    ScriptVersion: null),
                new CreateProcessNodeRequest(
                    "replacement-end",
                    "End",
                    "Replacement End",
                    RequiredCapability: null,
                    CommandName: null,
                    TimeoutSeconds: null,
                    InputPayload: null,
                    BlocklyWorkspaceJson: null,
                    ScriptSourceCode: null,
                    ScriptVersion: null)
            ],
            [
                new CreateProcessTransitionRequest(
                    "replacement-start-to-inspect",
                    "replacement-start",
                    "inspect",
                    Label: "inspect",
                    LoopPolicy: null,
                    MaxTraversals: null),
                new CreateProcessTransitionRequest(
                    "replacement-inspect-to-end",
                    "inspect",
                    "replacement-end",
                    Label: "complete",
                    LoopPolicy: null,
                    MaxTraversals: null)
            ]);
    }

    private static ProcessDefinitionService CreateService(FakeProcessDefinitionRepository repository)
    {
        return new ProcessDefinitionService(
            repository,
            new FixedClock(PublishedAtUtc.AddMinutes(5)),
            new FakeProcessScriptDefinitionValidator(ProcessScriptValidationReport.Valid));
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

    private sealed class FakeProcessDefinitionRepository : IProcessDefinitionRepository
    {
        private readonly Dictionary<ProcessDefinitionId, ProcessDefinition> _definitions = [];

        public FakeProcessDefinitionRepository(params ProcessDefinition[] definitions)
        {
            foreach (var definition in definitions)
            {
                _definitions.Add(definition.Id, definition);
            }
        }

        public int SaveCount { get; private set; }

        public ValueTask SaveAsync(
            ProcessDefinition definition,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            _definitions[definition.Id] = definition;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ProcessDefinition?> GetByIdAsync(
            ProcessDefinitionId definitionId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_definitions.GetValueOrDefault(definitionId));
        }

        public ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyCollection<ProcessDefinition>>(_definitions.Values.ToArray());
        }
    }

    private sealed class FakeProcessScriptDefinitionValidator : IProcessScriptDefinitionValidator
    {
        private readonly ProcessScriptValidationReport _report;

        public FakeProcessScriptDefinitionValidator(ProcessScriptValidationReport report)
        {
            _report = report;
        }

        public ValueTask<ProcessScriptValidationReport> ValidateAsync(
            ProcessNode node,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_report);
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
