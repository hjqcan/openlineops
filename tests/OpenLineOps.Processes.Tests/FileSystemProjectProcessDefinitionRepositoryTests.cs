using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.Processes.Tests;

public sealed class FileSystemProjectProcessDefinitionRepositoryTests : IDisposable
{
    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-process-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FlowBlocklyAndPythonSurviveNewRepositoryInstanceAndIsolateApplications()
    {
        var applicationA = Scope("application.a");
        var applicationB = Scope("application.b");
        var definitionId = new ProcessDefinitionId("process.main");
        var definitionA = CreateDefinition(
            definitionId,
            "Application A Flow",
            blocklySource: "result = {'application': 'A', 'mode': 'blockly'}\n",
            manualSource: "result = {'application': 'A', 'mode': 'manual'}\n");
        Assert.True(definitionA.Publish(new DateTimeOffset(2026, 7, 10, 4, 5, 0, TimeSpan.Zero)).Succeeded);
        var definitionB = CreateDefinition(
            definitionId,
            "Application B Flow",
            blocklySource: "result = {'application': 'B', 'mode': 'blockly'}\n",
            manualSource: "result = {'application': 'B', 'mode': 'manual'}\n");

        var writer = new FileSystemProjectProcessDefinitionRepository();
        await writer.SaveAsync(applicationA, definitionA);
        await writer.SaveAsync(applicationB, definitionB);

        var reader = new FileSystemProjectProcessDefinitionRepository();
        var restoredA = await reader.GetByIdAsync(applicationA, definitionId);
        var restoredB = await reader.GetByIdAsync(applicationB, definitionId);

        Assert.NotNull(restoredA);
        Assert.Equal("Application A Flow", restoredA.DisplayName);
        Assert.Equal(ProcessDefinitionStatus.Published, restoredA.Status);
        Assert.Equal(4, restoredA.Nodes.Count);
        Assert.Equal(
            "result = {'application': 'A', 'mode': 'blockly'}\n",
            restoredA.Nodes.Single(node => node.Id.Value == "blockly").ScriptSourceCode);
        Assert.Equal(
            """{"blocks":{"languageVersion":0,"blocks":[{"type":"controls_if"}]}}""",
            restoredA.Nodes.Single(node => node.Id.Value == "blockly").BlocklyWorkspaceJson);
        Assert.Equal(
            ProcessScriptEditorMode.ManualCode,
            restoredA.Nodes.Single(node => node.Id.Value == "manual").ScriptEditorMode);
        Assert.Equal(
            "result = {'application': 'A', 'mode': 'manual'}\n",
            restoredA.Nodes.Single(node => node.Id.Value == "manual").ScriptSourceCode);

        Assert.NotNull(restoredB);
        Assert.Equal("Application B Flow", restoredB.DisplayName);
        Assert.Equal(ProcessDefinitionStatus.Draft, restoredB.Status);
        Assert.Equal(
            "result = {'application': 'B', 'mode': 'blockly'}\n",
            restoredB.Nodes.Single(node => node.Id.Value == "blockly").ScriptSourceCode);

        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "flow.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "workspace.*.blockly.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "generated.*.py", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "source.*.py", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task TamperedPythonArtifactIsRejected()
    {
        var scope = Scope("application.tamper");
        var definitionId = new ProcessDefinitionId("process.tamper");
        var repository = new FileSystemProjectProcessDefinitionRepository();
        await repository.SaveAsync(
            scope,
            CreateDefinition(
                definitionId,
                "Tamper Flow",
                "result = {'trusted': True}\n",
                "result = {'manual': True}\n"));

        var generatedSourcePath = Assert.Single(
            Directory.GetFiles(_projectDirectory, "generated.*.py", SearchOption.AllDirectories));
        await File.AppendAllTextAsync(generatedSourcePath, "# tampered\n");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new FileSystemProjectProcessDefinitionRepository().GetByIdAsync(scope, definitionId));

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDirectory))
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
    }

    private ProjectApplicationWorkspaceScope Scope(string applicationId)
    {
        return new ProjectApplicationWorkspaceScope("project.process", applicationId, _projectDirectory);
    }

    private static ProcessDefinition CreateDefinition(
        ProcessDefinitionId definitionId,
        string displayName,
        string blocklySource,
        string manualSource)
    {
        var definition = ProcessDefinition.Create(
            definitionId,
            new ProcessVersionId($"{definitionId.Value}@1.0.0"),
            displayName,
            new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero));

        AddNode(definition, ProcessNode.Start(new ProcessNodeId("start"), "Start"));
        AddNode(definition, ProcessNode.PythonScript(
            new ProcessNodeId("blockly"),
            "Blockly Step",
            ProcessScriptEditorMode.Blockly,
            """{"blocks":{"languageVersion":0,"blocks":[{"type":"controls_if"}]}}""",
            blocklySource,
            scriptVersion: "1",
            scriptTimeout: TimeSpan.FromSeconds(10),
            inputPayload: "{\"serial\":\"SN-1\"}"));
        AddNode(definition, ProcessNode.PythonScript(
            new ProcessNodeId("manual"),
            "Manual Step",
            ProcessScriptEditorMode.ManualCode,
            blocklyWorkspaceJson: null,
            sourceCode: manualSource,
            scriptVersion: "2",
            scriptTimeout: TimeSpan.FromSeconds(20)));
        AddNode(definition, ProcessNode.End(new ProcessNodeId("end"), "End"));
        AddTransition(definition, "start-to-blockly", "start", "blockly");
        AddTransition(definition, "blockly-to-manual", "blockly", "manual");
        AddTransition(definition, "manual-to-end", "manual", "end");

        return definition;
    }

    private static void AddNode(ProcessDefinition definition, ProcessNode node)
    {
        var result = definition.AddNode(node);
        Assert.True(result.Succeeded, result.Message);
    }

    private static void AddTransition(
        ProcessDefinition definition,
        string transitionId,
        string fromNodeId,
        string toNodeId)
    {
        var result = definition.AddTransition(ProcessTransition.Create(
            new ProcessTransitionId(transitionId),
            new ProcessNodeId(fromNodeId),
            new ProcessNodeId(toNodeId)));
        Assert.True(result.Succeeded, result.Message);
    }
}
