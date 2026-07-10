using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.Processes.Tests;

public sealed class FileSystemProjectProcessBlocklyBlockDefinitionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset FirstRecordedAtUtc =
        new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondRecordedAtUtc = FirstRecordedAtUtc.AddMinutes(5);
    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-process-block-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VersionsSurviveNewRepositoryInstancesAndProjectMoveAndIsolateApplications()
    {
        const string blockType = "user_shared_fixture_action";
        var applicationA = Scope("application.a", _projectDirectory);
        var applicationB = Scope("application.b", _projectDirectory);
        var writer = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();

        var applicationAFirst = await SaveNewVersionAsync(
            writer,
            applicationA,
            blockType,
            "Fixture",
            "Application A Fixture Action",
            BlockJson(blockType, "application A fixture action v1"),
            "test.fixture.application-a.v1",
            FirstRecordedAtUtc);
        var applicationASecond = await SaveNewVersionAsync(
            writer,
            applicationA,
            blockType,
            "Fixture",
            "Application A Fixture Action V2",
            BlockJson(blockType, "application A fixture action v2"),
            "test.fixture.application-a.v2",
            SecondRecordedAtUtc);
        var applicationBFirst = await SaveNewVersionAsync(
            writer,
            applicationB,
            blockType,
            "Fixture",
            "Application B Fixture Action",
            BlockJson(blockType, "application B fixture action"),
            "test.fixture.application-b.v1",
            SecondRecordedAtUtc);

        Assert.Equal(1, applicationAFirst.Version);
        Assert.Equal(2, applicationASecond.Version);
        Assert.Equal(FirstRecordedAtUtc, applicationASecond.CreatedAtUtc);
        Assert.Equal(SecondRecordedAtUtc, applicationASecond.UpdatedAtUtc);
        Assert.Equal(1, applicationBFirst.Version);
        Assert.Equal(SecondRecordedAtUtc, applicationBFirst.CreatedAtUtc);

        var restartedRepository = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();
        await AssertApplicationsAreIsolatedAsync(
            restartedRepository,
            applicationA,
            applicationB,
            blockType);

        Directory.Move(_projectDirectory, MovedProjectDirectory);
        var movedRepository = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();
        await AssertApplicationsAreIsolatedAsync(
            movedRepository,
            Scope("application.a", MovedProjectDirectory),
            Scope("application.b", MovedProjectDirectory),
            blockType);
    }

    [Fact]
    public async Task TamperedApplicationIdentityIsRejected()
    {
        const string blockType = "user_identity_tamper";
        var scope = Scope("application.identity", _projectDirectory);
        var repository = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();
        await SaveNewVersionAsync(
            repository,
            scope,
            blockType,
            "Fixture",
            "Identity Test",
            BlockJson(blockType, "identity test"),
            "test.fixture.identity",
            FirstRecordedAtUtc);
        var documentPath = FindBlockDocumentPath(_projectDirectory, blockType);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(documentPath))!.AsObject();
        Assert.True(document.ContainsKey("applicationId"));
        document["applicationId"] = "application.other";
        await File.WriteAllTextAsync(documentPath, document.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FileSystemProjectProcessBlocklyBlockDefinitionRepository()
                .GetLatestAsync(scope, blockType)
                .AsTask());

        Assert.Contains("application", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidBlockDocumentJsonIsRejected()
    {
        const string blockType = "user_invalid_json";
        var scope = Scope("application.invalid-json", _projectDirectory);
        var repository = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();
        await SaveNewVersionAsync(
            repository,
            scope,
            blockType,
            "Fixture",
            "Invalid JSON Test",
            BlockJson(blockType, "invalid json test"),
            "test.fixture.invalid-json",
            FirstRecordedAtUtc);
        var documentPath = FindBlockDocumentPath(_projectDirectory, blockType);
        await File.WriteAllTextAsync(documentPath, "{ this-is-not-json");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FileSystemProjectProcessBlocklyBlockDefinitionRepository()
                .GetLatestAsync(scope, blockType)
                .AsTask());

        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ObsoleteCustomBlockSchemaIsRejected()
    {
        const string blockType = "user_obsolete_schema";
        var scope = Scope("application.current-only", _projectDirectory);
        var repository = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();
        await SaveNewVersionAsync(
            repository,
            scope,
            blockType,
            "Fixture",
            "Current Only",
            BlockJson(blockType, "current only"),
            "test.fixture.current-only",
            FirstRecordedAtUtc);
        var path = FindBlockDocumentPath(_projectDirectory, blockType);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["schemaVersion"] = 1;
        await File.WriteAllTextAsync(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.GetLatestAsync(scope, blockType).AsTask());
    }

    [Fact]
    public async Task RemovedHostProjectIdFieldIsRejectedFromCustomBlockResource()
    {
        const string blockType = "user_removed_host_project";
        var scope = Scope("application.strict", _projectDirectory);
        var repository = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();
        await SaveNewVersionAsync(
            repository,
            scope,
            blockType,
            "Fixture",
            "Strict",
            BlockJson(blockType, "strict"),
            "test.fixture.strict",
            FirstRecordedAtUtc);
        var path = FindBlockDocumentPath(_projectDirectory, blockType);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["projectId"] = "removed-host-project";
        await File.WriteAllTextAsync(path, document.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.GetLatestAsync(scope, blockType).AsTask());
    }

    [Fact]
    public async Task EditableCustomBlocksAreStoredBesideTheApplicationProjectFile()
    {
        const string blockType = "user/custom fixture action";
        var scope = new ProjectApplicationWorkspaceScope(
            "project.process-blocks",
            "application.id-does-not-name-the-folder",
            _projectDirectory,
            "applications/Operator Cell/Main Line.oloapp");
        var repository = new FileSystemProjectProcessBlocklyBlockDefinitionRepository();

        await SaveNewVersionAsync(
            repository,
            scope,
            blockType,
            "Fixture",
            "Custom Root Fixture Action",
            BlockJson(blockType, "custom root fixture action"),
            "test.fixture.custom-root",
            FirstRecordedAtUtc);

        var versionPath = Assert.Single(Directory.GetFiles(
            Path.Combine(scope.ApplicationRootPath, "blocks", "custom"),
            "version-*.json",
            SearchOption.AllDirectories));
        var versionsDirectory = Directory.GetParent(versionPath)!;
        var blockDirectory = versionsDirectory.Parent!;

        Assert.Equal("versions", versionsDirectory.Name);
        Assert.Equal(
            Path.Combine(scope.ApplicationRootPath, "blocks", "custom"),
            blockDirectory.Parent!.FullName);
        Assert.StartsWith("block-user-custom-fixture-action--", blockDirectory.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDirectory))
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }

        if (Directory.Exists(MovedProjectDirectory))
        {
            Directory.Delete(MovedProjectDirectory, recursive: true);
        }
    }

    private static async Task AssertApplicationsAreIsolatedAsync(
        FileSystemProjectProcessBlocklyBlockDefinitionRepository repository,
        ProjectApplicationWorkspaceScope applicationA,
        ProjectApplicationWorkspaceScope applicationB,
        string blockType)
    {
        var latestA = await repository.GetLatestAsync(applicationA, blockType);
        var latestB = await repository.GetLatestAsync(applicationB, blockType);
        var listedA = await repository.ListLatestAsync(applicationA);
        var listedB = await repository.ListLatestAsync(applicationB);
        var versionsA = await repository.ListVersionsAsync(applicationA, blockType);
        var versionsB = await repository.ListVersionsAsync(applicationB, blockType);

        Assert.NotNull(latestA);
        Assert.Equal(2, latestA.Version);
        Assert.Equal("Application A Fixture Action V2", latestA.DisplayName);
        Assert.Equal(BlockJson(blockType, "application A fixture action v2"), latestA.BlocklyJson);
        AssertCanonicalContract(latestA, "test.fixture.application-a.v2");
        Assert.Equal(FirstRecordedAtUtc, latestA.CreatedAtUtc);
        Assert.Equal(SecondRecordedAtUtc, latestA.UpdatedAtUtc);
        Assert.Single(listedA);
        Assert.Equal(2, listedA.Single().Version);
        Assert.Collection(
            versionsA,
            block =>
            {
                Assert.Equal(2, block.Version);
                Assert.Equal("Application A Fixture Action V2", block.DisplayName);
                AssertCanonicalContract(block, "test.fixture.application-a.v2");
            },
            block =>
            {
                Assert.Equal(1, block.Version);
                Assert.Equal("Application A Fixture Action", block.DisplayName);
                AssertCanonicalContract(block, "test.fixture.application-a.v1");
            });

        Assert.NotNull(latestB);
        Assert.Equal(1, latestB.Version);
        Assert.Equal("Application B Fixture Action", latestB.DisplayName);
        Assert.Equal(BlockJson(blockType, "application B fixture action"), latestB.BlocklyJson);
        AssertCanonicalContract(latestB, "test.fixture.application-b.v1");
        Assert.Equal(SecondRecordedAtUtc, latestB.CreatedAtUtc);
        Assert.Equal(SecondRecordedAtUtc, latestB.UpdatedAtUtc);
        Assert.Single(listedB);
        Assert.Equal(1, listedB.Single().Version);
        Assert.Collection(
            versionsB,
            block =>
            {
                Assert.Equal(1, block.Version);
                AssertCanonicalContract(block, "test.fixture.application-b.v1");
            });
    }

    private static ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
        FileSystemProjectProcessBlocklyBlockDefinitionRepository repository,
        ProjectApplicationWorkspaceScope scope,
        string blockType,
        string category,
        string displayName,
        string blocklyJson,
        string actionType,
        DateTimeOffset recordedAtUtc)
    {
        var contract = CreateContract(actionType);
        return repository.SaveNewVersionAsync(
            scope,
            blockType,
            category,
            displayName,
            blocklyJson,
            ProcessBlocklyBlockExecutionModes.DeclarativeActionContract,
            contract.SchemaVersion,
            contract.CanonicalJson,
            contract.Sha256,
            recordedAtUtc);
    }

    private static RuntimeActionContractCanonicalArtifact CreateContract(string actionType)
    {
        var result = new RuntimeActionContractCanonicalSerializer().Serialize(new RuntimeActionContract(
            RuntimeActionContractSchemaVersions.V1,
            actionType,
            new Dictionary<string, RuntimeActionFieldDefinition>(StringComparer.Ordinal),
            new RuntimeDelayEmit(new RuntimeActionLiteralValue(JsonSerializer.SerializeToElement(1)))));

        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
    }

    private static void AssertCanonicalContract(
        ProcessBlocklyBlockDefinitionRecord block,
        string expectedActionType)
    {
        Assert.Equal(ProcessBlocklyBlockExecutionModes.DeclarativeActionContract, block.ExecutionMode);
        Assert.Equal(RuntimeActionContractSchemaVersions.V1, block.RuntimeActionContractSchemaVersion);
        var serializer = new RuntimeActionContractCanonicalSerializer();
        var parsed = serializer.Deserialize(block.RuntimeActionContractJson);
        Assert.True(parsed.IsSuccess, parsed.Error.Message);
        Assert.Equal(expectedActionType, parsed.Value.ActionType);
        var canonical = serializer.Serialize(parsed.Value);
        Assert.True(canonical.IsSuccess, canonical.Error.Message);
        Assert.Equal(block.RuntimeActionContractJson, canonical.Value.CanonicalJson);
        Assert.Equal(block.RuntimeActionContractSha256, canonical.Value.Sha256);
    }

    private static ProjectApplicationWorkspaceScope Scope(string applicationId, string projectDirectory)
    {
        return new ProjectApplicationWorkspaceScope(
            "project.process-blocks",
            applicationId,
            projectDirectory,
            $"applications/{applicationId}/{applicationId}.oloapp");
    }

    private static string BlockJson(string blockType, string message)
    {
        return $$"""{"type":"{{blockType}}","message0":"{{message}}","previousStatement":null,"nextStatement":null}""";
    }

    private static string FindBlockDocumentPath(string projectDirectory, string blockType)
    {
        var matches = Directory
            .GetFiles(projectDirectory, "*.json", SearchOption.AllDirectories)
            .Where(path => IsBlockDocument(path, blockType))
            .ToArray();

        return Assert.Single(matches);
    }

    private static bool IsBlockDocument(string path, string blockType)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("blockType", out var persistedBlockType)
                && persistedBlockType.GetString() == blockType;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string MovedProjectDirectory => $"{_projectDirectory}-moved";
}
