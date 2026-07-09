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
            ProcessScriptEditorMode.Blockly,
            """{"blocks":{"languageVersion":0}}""",
            "result = {'normalized': input_payload}",
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
