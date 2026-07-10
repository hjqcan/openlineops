using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
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

        var applicationAFirst = await writer.SaveNewVersionAsync(
            applicationA,
            blockType,
            "Fixture",
            "Application A Fixture Action",
            BlockJson(blockType, "application A fixture action v1"),
            "automation_plan.append({'application': 'A', 'revision': 1})",
            FirstRecordedAtUtc);
        var applicationASecond = await writer.SaveNewVersionAsync(
            applicationA,
            blockType,
            "Fixture",
            "Application A Fixture Action V2",
            BlockJson(blockType, "application A fixture action v2"),
            "automation_plan.append({'application': 'A', 'revision': 2})",
            SecondRecordedAtUtc);
        var applicationBFirst = await writer.SaveNewVersionAsync(
            applicationB,
            blockType,
            "Fixture",
            "Application B Fixture Action",
            BlockJson(blockType, "application B fixture action"),
            "automation_plan.append({'application': 'B', 'revision': 1})",
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
        await repository.SaveNewVersionAsync(
            scope,
            blockType,
            "Fixture",
            "Identity Test",
            BlockJson(blockType, "identity test"),
            "automation_plan.append({'identity': 'trusted'})",
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
        await repository.SaveNewVersionAsync(
            scope,
            blockType,
            "Fixture",
            "Invalid JSON Test",
            BlockJson(blockType, "invalid json test"),
            "automation_plan.append({'json': 'trusted'})",
            FirstRecordedAtUtc);
        var documentPath = FindBlockDocumentPath(_projectDirectory, blockType);
        await File.WriteAllTextAsync(documentPath, "{ this-is-not-json");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FileSystemProjectProcessBlocklyBlockDefinitionRepository()
                .GetLatestAsync(scope, blockType)
                .AsTask());

        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("'application': 'A'", latestA.PythonCodeTemplate);
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
            },
            block =>
            {
                Assert.Equal(1, block.Version);
                Assert.Equal("Application A Fixture Action", block.DisplayName);
            });

        Assert.NotNull(latestB);
        Assert.Equal(1, latestB.Version);
        Assert.Equal("Application B Fixture Action", latestB.DisplayName);
        Assert.Equal(BlockJson(blockType, "application B fixture action"), latestB.BlocklyJson);
        Assert.Contains("'application': 'B'", latestB.PythonCodeTemplate);
        Assert.Equal(SecondRecordedAtUtc, latestB.CreatedAtUtc);
        Assert.Equal(SecondRecordedAtUtc, latestB.UpdatedAtUtc);
        Assert.Single(listedB);
        Assert.Equal(1, listedB.Single().Version);
        Assert.Collection(versionsB, block => Assert.Equal(1, block.Version));
    }

    private static ProjectApplicationWorkspaceScope Scope(string applicationId, string projectDirectory)
    {
        return new ProjectApplicationWorkspaceScope("project.process-blocks", applicationId, projectDirectory);
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
