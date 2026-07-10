using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Application.Scripting;
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
        Assert.Null(restoredA.Nodes.Single(node => node.Id.Value == "blockly").ScriptSourceCode);
        Assert.Equal(
            """{"blocks":{"languageVersion":0,"blocks":[{"type":"controls_if"}]}}""",
            restoredA.Nodes.Single(node => node.Id.Value == "blockly").BlocklyWorkspaceJson);
        Assert.Null(restoredA.Nodes.Single(node => node.Id.Value == "manual").BlocklyWorkspaceJson);
        Assert.Equal(
            "result = {'application': 'A', 'mode': 'manual'}\n",
            restoredA.Nodes.Single(node => node.Id.Value == "manual").ScriptSourceCode);

        Assert.NotNull(restoredB);
        Assert.Equal("Application B Flow", restoredB.DisplayName);
        Assert.Equal(ProcessDefinitionStatus.Draft, restoredB.Status);
        Assert.Null(restoredB.Nodes.Single(node => node.Id.Value == "blockly").ScriptSourceCode);

        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "flow.json", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(_projectDirectory, "workspace.*.blockly.json", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetFiles(_projectDirectory, "generated.*.py", SearchOption.AllDirectories));
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

        var sourcePath = Assert.Single(
            Directory.GetFiles(_projectDirectory, "source.*.py", SearchOption.AllDirectories));
        await File.AppendAllTextAsync(sourcePath, "# tampered\n");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new FileSystemProjectProcessDefinitionRepository().GetByIdAsync(scope, definitionId));

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedFlowFormatIsRejected()
    {
        var scope = Scope("application.current-only");
        var definitionId = new ProcessDefinitionId("process.current-only");
        var repository = new FileSystemProjectProcessDefinitionRepository();
        await repository.SaveAsync(
            scope,
            CreateDefinition(
                definitionId,
                "Current Only",
                "result = {'current': True}\n",
                "result = {'manual': True}\n"));

        var path = Assert.Single(Directory.GetFiles(
            scope.ApplicationRootPath,
            "flow.json",
            SearchOption.AllDirectories));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["formatVersion"] = 99;
        await File.WriteAllTextAsync(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, definitionId));
    }

    [Fact]
    public async Task RemovedHostProjectIdFieldIsRejectedFromFlowResource()
    {
        var scope = Scope("application.strict");
        var definitionId = new ProcessDefinitionId("process.strict");
        var repository = new FileSystemProjectProcessDefinitionRepository();
        await repository.SaveAsync(
            scope,
            CreateDefinition(
                definitionId,
                "Strict",
                "result = {'strict': True}\n",
                "result = {'manual': True}\n"));

        var path = Assert.Single(Directory.GetFiles(
            scope.ApplicationRootPath,
            "flow.json",
            SearchOption.AllDirectories));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["projectId"] = "removed-host-project";
        await File.WriteAllTextAsync(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, definitionId));
    }

    [Fact]
    public async Task CompleteApplicationProcessResourcesAreByteCopyableAcrossProjects()
    {
        const string applicationId = "application.portable";
        const string blockType = "user_portable_fixture";
        var sourceScope = new ProjectApplicationWorkspaceScope(
            "project.source",
            applicationId,
            Path.Combine(_projectDirectory, "project-source"),
            "applications/Source Cell/Portable.oloapp");
        var destinationScope = new ProjectApplicationWorkspaceScope(
            "project.destination",
            applicationId,
            Path.Combine(_projectDirectory, "project-destination"),
            "applications/Imported Cell/Imported.oloapp");
        var definitionId = new ProcessDefinitionId("process.portable");
        var processRepository = new FileSystemProjectProcessDefinitionRepository();
        var blockRepository = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();

        await processRepository.SaveAsync(
            sourceScope,
            CreateDefinition(
                definitionId,
                "Portable Process",
                "result = {'portable': 'blockly'}\n",
                "result = {'portable': 'manual'}\n"));
        var contract = CreateWaitContractArtifact();
        await blockRepository.SaveNewVersionAsync(
            sourceScope,
            blockType,
            "Fixture",
            "Portable Fixture",
            $$"""{"type":"{{blockType}}","message0":"portable fixture","previousStatement":null,"nextStatement":null}""",
            ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            contract.SchemaVersion,
            contract.CanonicalJson,
            contract.Sha256,
            new DateTimeOffset(2026, 7, 10, 7, 0, 0, TimeSpan.Zero));

        var jsonPaths = Directory.GetFiles(
            sourceScope.ApplicationRootPath,
            "*.json",
            SearchOption.AllDirectories);
        Assert.NotEmpty(jsonPaths);
        Assert.All(jsonPaths, path =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(document.RootElement.TryGetProperty("projectId", out _));
        });
        using (var flowDocument = JsonDocument.Parse(File.ReadAllText(Assert.Single(
                   Directory.GetFiles(sourceScope.ApplicationRootPath, "flow.json", SearchOption.AllDirectories)))))
        {
            Assert.Equal(1, flowDocument.RootElement.GetProperty("formatVersion").GetInt32());
        }

        using (var blockDocument = JsonDocument.Parse(File.ReadAllText(Assert.Single(
                   jsonPaths,
                   path => Path.GetFileName(path).StartsWith("version-", StringComparison.Ordinal)))))
        {
            Assert.Equal(1, blockDocument.RootElement.GetProperty("schemaVersion").GetInt32());
        }

        CopyDirectory(sourceScope.ApplicationRootPath, destinationScope.ApplicationRootPath);
        AssertByteIdenticalTrees(sourceScope.ApplicationRootPath, destinationScope.ApplicationRootPath);

        var restoredProcess = await new FileSystemProjectProcessDefinitionRepository()
            .GetByIdAsync(destinationScope, definitionId);
        var restoredBlock = await new FileSystemProjectProcessBlocklyBlockDefinitionRepository()
            .GetLatestAsync(destinationScope, blockType);

        Assert.NotNull(restoredProcess);
        Assert.Equal("Portable Process", restoredProcess.DisplayName);
        Assert.Null(restoredProcess.Nodes.Single(node => node.Id.Value == "blockly").ScriptSourceCode);
        Assert.Equal(
            """{"blocks":{"languageVersion":0,"blocks":[{"type":"controls_if"}]}}""",
            restoredProcess.Nodes.Single(node => node.Id.Value == "blockly").BlocklyWorkspaceJson);
        Assert.Equal(
            "result = {'portable': 'manual'}\n",
            restoredProcess.Nodes.Single(node => node.Id.Value == "manual").ScriptSourceCode);
        Assert.NotNull(restoredBlock);
        Assert.Equal("Portable Fixture", restoredBlock.DisplayName);
        Assert.Equal(contract.Sha256, restoredBlock.RuntimeActionContractSha256);
    }

    [Fact]
    public async Task EditableFlowResourcesAreStoredBesideTheApplicationProjectFile()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.process",
            "application.id-does-not-name-the-folder",
            _projectDirectory,
            "applications/Operator Cell/Main Line.oloapp");
        var definitionId = new ProcessDefinitionId("process.custom/root");
        var repository = new FileSystemProjectProcessDefinitionRepository();

        await repository.SaveAsync(
            scope,
            CreateDefinition(
                definitionId,
                "Custom Root Flow",
                "result = {'mode': 'blockly'}\n",
                "result = {'mode': 'manual'}\n"));

        var flowPath = Assert.Single(Directory.GetFiles(
            Path.Combine(scope.ApplicationRootPath, "flows"),
            "flow.json",
            SearchOption.AllDirectories));
        var flowDirectory = Path.GetDirectoryName(flowPath);

        Assert.NotNull(flowDirectory);
        Assert.Equal(
            Path.Combine(scope.ApplicationRootPath, "flows"),
            Directory.GetParent(flowDirectory)!.FullName);
        Assert.StartsWith("process-process.custom-root--", Path.GetFileName(flowDirectory));
        Assert.Single(Directory.GetFiles(
            flowDirectory,
            "*.py",
            SearchOption.AllDirectories));
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
        return new ProjectApplicationWorkspaceScope(
            "project.process",
            applicationId,
            _projectDirectory,
            $"applications/{applicationId}/{applicationId}.oloapp");
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
        AddNode(definition, ProcessNode.Blockly(
            new ProcessNodeId("blockly"),
            "Blockly Step",
            """{"blocks":{"languageVersion":0,"blocks":[{"type":"controls_if"}]}}""",
            executionTimeout: TimeSpan.FromSeconds(10),
            inputPayload: "{\"serial\":\"SN-1\"}"));
        AddNode(definition, ProcessNode.PythonScript(
            new ProcessNodeId("manual"),
            "Manual Step",
            sourceCode: manualSource,
            scriptVersion: "2",
            scriptTimeout: TimeSpan.FromSeconds(20)));
        AddNode(definition, ProcessNode.End(new ProcessNodeId("end"), "End"));
        AddTransition(definition, "start-to-blockly", "start", "blockly");
        AddTransition(definition, "blockly-to-manual", "blockly", "manual");
        AddTransition(definition, "manual-to-end", "manual", "end");

        return definition;
    }

    private static RuntimeActionContractCanonicalArtifact CreateWaitContractArtifact()
    {
        var contract = new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            "fixture.wait",
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal)
            {
                ["DURATION_MS"] = new(
                    RuntimeActionFieldType.WholeNumber,
                    Required: true,
                    Minimum: 0)
            },
            new RuntimeDelayEmit(new RuntimeActionFieldValue("DURATION_MS")));
        var result = new RuntimeActionContractCanonicalSerializer().Serialize(contract);
        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
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

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var directory in Directory.EnumerateDirectories(
                     sourcePath,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destinationPath,
                Path.GetRelativePath(sourcePath, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var destinationFile = Path.Combine(
                destinationPath,
                Path.GetRelativePath(sourcePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile);
        }
    }

    private static void AssertByteIdenticalTrees(string expectedRoot, string actualRoot)
    {
        var expectedFiles = Directory.EnumerateFiles(expectedRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(expectedRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualFiles = Directory.EnumerateFiles(actualRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(actualRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedFiles, actualFiles);
        Assert.All(expectedFiles, relativePath => Assert.Equal(
            File.ReadAllBytes(Path.Combine(expectedRoot, relativePath)),
            File.ReadAllBytes(Path.Combine(actualRoot, relativePath))));
    }
}
