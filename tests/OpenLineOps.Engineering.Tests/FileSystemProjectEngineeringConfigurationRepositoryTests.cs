using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Operations;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;
using OpenLineOps.Engineering.Infrastructure.Persistence;

namespace OpenLineOps.Engineering.Tests;

public sealed class FileSystemProjectEngineeringConfigurationRepositoryTests : IDisposable
{
    private const string WorkspaceIdValue = "workspace.main";
    private const string EngineeringProjectIdValue = "engineering.main";
    private const string RecipeIdValue = "recipe.main";
    private const string RecipeVersionIdValue = "recipe.main@1.0.0";
    private const string StationProfileIdValue = "station.main";
    private const string SnapshotIdValue = "snapshot.main";
    private const string ProcessDefinitionIdValue = "process.main";
    private static readonly DateTimeOffset BaseCreatedAtUtc =
        new(2026, 7, 10, 7, 0, 0, TimeSpan.Zero);
    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-engineering-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConfigurationSurvivesNewRepositoryInstancesAndProjectMoveAndIsolatesApplications()
    {
        var applicationA = Scope("application.a", _projectDirectory);
        var applicationB = Scope("application.b", _projectDirectory);
        var configurationA = CreateConfiguration(
            "Application A",
            BaseCreatedAtUtc,
            BaseCreatedAtUtc.AddMinutes(10),
            processVersionId: "process.main@1.0.0",
            voltageMax: "5.1",
            speed: "120",
            primaryCapability: "device.scanner",
            primaryDevice: "scanner-a",
            secondaryCapability: "fixture.clamp",
            secondaryDevice: "clamp-a");
        var configurationB = CreateConfiguration(
            "Application B",
            BaseCreatedAtUtc.AddHours(1),
            BaseCreatedAtUtc.AddHours(1).AddMinutes(10),
            processVersionId: "process.main@2.0.0",
            voltageMax: "7.4",
            speed: "80",
            primaryCapability: "device.camera",
            primaryDevice: "camera-b",
            secondaryCapability: "io.trigger",
            secondaryDevice: "io-b");
        var writer = new FileSystemProjectEngineeringConfigurationRepository();

        await SaveAsync(writer, applicationA, configurationA);
        await SaveAsync(writer, applicationB, configurationB);

        var restartedRepository = new FileSystemProjectEngineeringConfigurationRepository();
        await AssertConfigurationAsync(restartedRepository, applicationA, configurationA);
        await AssertConfigurationAsync(restartedRepository, applicationB, configurationB);

        Directory.Move(_projectDirectory, MovedProjectDirectory);
        var movedRepository = new FileSystemProjectEngineeringConfigurationRepository();
        await AssertConfigurationAsync(
            movedRepository,
            Scope("application.a", MovedProjectDirectory),
            configurationA);
        await AssertConfigurationAsync(
            movedRepository,
            Scope("application.b", MovedProjectDirectory),
            configurationB);
    }

    [Fact]
    public async Task TamperedApplicationIdentityIsRejected()
    {
        var scope = Scope("application.identity", _projectDirectory);
        var configuration = CreateConfiguration(
            "Identity",
            BaseCreatedAtUtc,
            BaseCreatedAtUtc.AddMinutes(10),
            "process.main@identity",
            "5.0",
            "100",
            "device.identity.primary",
            "identity-primary",
            "device.identity.secondary",
            "identity-secondary");
        var repository = new FileSystemProjectEngineeringConfigurationRepository();
        await repository.SaveAsync(scope, configuration.Workspace);
        var documentPath = FindDocumentPath(_projectDirectory, "resourceId", WorkspaceIdValue);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(documentPath))!.AsObject();
        Assert.True(document.ContainsKey("applicationId"));
        document["applicationId"] = "application.other";
        await File.WriteAllTextAsync(documentPath, document.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FileSystemProjectEngineeringConfigurationRepository().GetByIdAsync(
                scope,
                new WorkspaceId(WorkspaceIdValue)));

        Assert.Contains("application", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidRecipeDocumentJsonIsRejected()
    {
        var scope = Scope("application.invalid-json", _projectDirectory);
        var configuration = CreateConfiguration(
            "Invalid JSON",
            BaseCreatedAtUtc,
            BaseCreatedAtUtc.AddMinutes(10),
            "process.main@invalid-json",
            "5.0",
            "100",
            "device.json.primary",
            "json-primary",
            "device.json.secondary",
            "json-secondary");
        var repository = new FileSystemProjectEngineeringConfigurationRepository();
        await repository.SaveAsync(scope, configuration.Recipe);
        var documentPath = FindDocumentPath(_projectDirectory, "resourceId", RecipeIdValue);
        await File.WriteAllTextAsync(documentPath, "{ this-is-not-json");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new FileSystemProjectEngineeringConfigurationRepository().GetByIdAsync(
                scope,
                new RecipeId(RecipeIdValue)));

        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditableConfigurationIsStoredBesideTheApplicationProjectFile()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.engineering",
            "application.id-does-not-name-the-folder",
            _projectDirectory,
            "applications/Operator Cell/Main Line.oloapp");
        var configuration = CreateConfiguration(
            "Custom Root",
            BaseCreatedAtUtc,
            BaseCreatedAtUtc.AddMinutes(10),
            "process.main@custom-root",
            "5.0",
            "100",
            "device.custom.primary",
            "custom-primary",
            "device.custom.secondary",
            "custom-secondary");
        var repository = new FileSystemProjectEngineeringConfigurationRepository();

        await SaveAsync(repository, scope, configuration);

        var configurationRoot = Path.Combine(scope.ApplicationRootPath, "configuration");
        var documents = Directory.GetFiles(
            configurationRoot,
            "*.json",
            SearchOption.AllDirectories);

        Assert.Equal(4, documents.Length);
        Assert.All(documents, path => Assert.StartsWith(
            configurationRoot + Path.DirectorySeparatorChar,
            path,
            StringComparison.OrdinalIgnoreCase));
        Assert.Single(Directory.GetFiles(
            Path.Combine(configurationRoot, "workspaces"),
            "workspace-*.json"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(configurationRoot, "projects"),
            "project-*.json"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(configurationRoot, "recipes"),
            "recipe-*.json"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(configurationRoot, "station-profiles"),
            "station-profile-*.json"));
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

    private static async Task SaveAsync(
        FileSystemProjectEngineeringConfigurationRepository repository,
        ProjectApplicationWorkspaceScope scope,
        EngineeringConfigurationFixture configuration)
    {
        await repository.SaveAsync(scope, configuration.Workspace);
        await repository.SaveAsync(scope, configuration.Recipe);
        await repository.SaveAsync(scope, configuration.StationProfile);
        await repository.SaveAsync(scope, configuration.Project);
    }

    private static async Task AssertConfigurationAsync(
        FileSystemProjectEngineeringConfigurationRepository repository,
        ProjectApplicationWorkspaceScope scope,
        EngineeringConfigurationFixture expected)
    {
        var workspace = await repository.GetByIdAsync(scope, expected.Workspace.Id);
        var recipe = await repository.GetByIdAsync(scope, expected.Recipe.Id);
        var station = await repository.GetByIdAsync(scope, expected.StationProfile.Id);
        var project = await repository.GetByIdAsync(scope, expected.Project.Id);
        var workspaces = await repository.ListWorkspacesAsync(scope);
        var recipes = await repository.ListRecipesAsync(scope);
        var stations = await repository.ListStationProfilesAsync(scope);
        var projects = await repository.ListProjectsAsync(scope);

        Assert.NotNull(workspace);
        Assert.Equal(expected.Workspace.Id, workspace.Id);
        Assert.Equal(expected.Workspace.DisplayName, workspace.DisplayName);
        Assert.Equal(expected.Workspace.CreatedAtUtc, workspace.CreatedAtUtc);
        Assert.Empty(workspace.DomainEvents);
        Assert.Single(workspaces);
        Assert.Equal(expected.Workspace.Id, workspaces.Single().Id);

        Assert.NotNull(recipe);
        Assert.Equal(expected.Recipe.Id, recipe.Id);
        Assert.Equal(expected.Recipe.VersionId, recipe.VersionId);
        Assert.Equal(expected.Recipe.DisplayName, recipe.DisplayName);
        Assert.Equal(RecipeStatus.Published, recipe.Status);
        Assert.Equal(expected.Recipe.CreatedAtUtc, recipe.CreatedAtUtc);
        Assert.Equal(expected.Recipe.PublishedAtUtc, recipe.PublishedAtUtc);
        Assert.Equal(expected.Recipe.Parameters.Count, recipe.Parameters.Count);
        foreach (var expectedParameter in expected.Recipe.Parameters)
        {
            Assert.Contains(recipe.Parameters, parameter =>
                parameter.Key == expectedParameter.Key
                && parameter.Value == expectedParameter.Value);
        }

        Assert.Empty(recipe.DomainEvents);
        Assert.Single(recipes);
        Assert.Equal(expected.Recipe.Id, recipes.Single().Id);

        Assert.NotNull(station);
        Assert.Equal(expected.StationProfile.Id, station.Id);
        Assert.Equal(expected.StationProfile.DisplayName, station.DisplayName);
        AssertBindings(expected.StationProfile.DeviceBindings, station.DeviceBindings);
        Assert.Single(stations);
        Assert.Equal(expected.StationProfile.Id, stations.Single().Id);

        Assert.NotNull(project);
        Assert.Equal(expected.Project.Id, project.Id);
        Assert.Equal(expected.Project.WorkspaceId, project.WorkspaceId);
        Assert.Equal(expected.Project.DisplayName, project.DisplayName);
        Assert.Equal(expected.Project.CreatedAtUtc, project.CreatedAtUtc);
        Assert.Equal(new ConfigurationSnapshotId(SnapshotIdValue), project.ActiveSnapshotId);
        Assert.Empty(project.DomainEvents);
        var expectedSnapshot = Assert.Single(expected.Project.Snapshots);
        var snapshot = Assert.Single(project.Snapshots);
        Assert.Equal(expectedSnapshot.Id, snapshot.Id);
        Assert.Equal(expectedSnapshot.ProjectId, snapshot.ProjectId);
        Assert.Equal(expectedSnapshot.ProcessDefinitionId, snapshot.ProcessDefinitionId);
        Assert.Equal(expectedSnapshot.ProcessVersionId, snapshot.ProcessVersionId);
        Assert.Equal(expectedSnapshot.RecipeId, snapshot.RecipeId);
        Assert.Equal(expectedSnapshot.RecipeVersionId, snapshot.RecipeVersionId);
        Assert.Equal(expectedSnapshot.StationProfileId, snapshot.StationProfileId);
        Assert.Equal(expectedSnapshot.Status, snapshot.Status);
        Assert.Equal(expectedSnapshot.PublishedAtUtc, snapshot.PublishedAtUtc);
        AssertBindingSnapshots(expectedSnapshot.DeviceBindings, snapshot.DeviceBindings);
        Assert.Single(projects);
        Assert.Equal(expected.Project.Id, projects.Single().Id);
    }

    private static EngineeringConfigurationFixture CreateConfiguration(
        string prefix,
        DateTimeOffset createdAtUtc,
        DateTimeOffset publishedAtUtc,
        string processVersionId,
        string voltageMax,
        string speed,
        string primaryCapability,
        string primaryDevice,
        string secondaryCapability,
        string secondaryDevice)
    {
        var workspace = Workspace.Create(
            new WorkspaceId(WorkspaceIdValue),
            $"{prefix} Engineering Workspace",
            createdAtUtc);
        var recipe = Recipe.Create(
            new RecipeId(RecipeIdValue),
            new RecipeVersionId(RecipeVersionIdValue),
            $"{prefix} Recipe",
            createdAtUtc.AddMinutes(1));
        AssertAccepted(recipe.AddOrUpdateParameter("voltage.max", voltageMax));
        AssertAccepted(recipe.AddOrUpdateParameter("axis.speed", speed));
        AssertAccepted(recipe.Publish(publishedAtUtc));

        var station = StationProfile.Create(
            new StationProfileId(StationProfileIdValue),
            $"{prefix} Station");
        AssertAccepted(station.AddDeviceBinding(DeviceBinding.Create(
            new DeviceBindingId("binding.primary"),
            new DeviceCapabilityId(primaryCapability),
            primaryDevice)));
        AssertAccepted(station.AddDeviceBinding(DeviceBinding.Create(
            new DeviceBindingId("binding.secondary"),
            new DeviceCapabilityId(secondaryCapability),
            secondaryDevice)));

        var project = EngineeringProject.Create(
            new EngineeringProjectId(EngineeringProjectIdValue),
            workspace.Id,
            $"{prefix} Engineering Project",
            createdAtUtc.AddMinutes(2));
        AssertAccepted(project.PublishSnapshot(
            new ConfigurationSnapshotId(SnapshotIdValue),
            new ProcessDefinitionId(ProcessDefinitionIdValue),
            new ProcessVersionId(processVersionId),
            recipe,
            station,
            publishedAtUtc.AddMinutes(1)));

        return new EngineeringConfigurationFixture(workspace, recipe, station, project);
    }

    private static void AssertBindings(
        IReadOnlyCollection<DeviceBinding> expected,
        IReadOnlyCollection<DeviceBinding> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var expectedBinding in expected)
        {
            var binding = Assert.Single(actual, candidate => candidate.Id == expectedBinding.Id);
            Assert.Equal(expectedBinding.CapabilityId, binding.CapabilityId);
            Assert.Equal(expectedBinding.DeviceKey, binding.DeviceKey);
        }
    }

    private static void AssertBindingSnapshots(
        IReadOnlyCollection<OpenLineOps.Engineering.Domain.Snapshots.DeviceBindingSnapshot> expected,
        IReadOnlyCollection<OpenLineOps.Engineering.Domain.Snapshots.DeviceBindingSnapshot> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var expectedBinding in expected)
        {
            var binding = Assert.Single(
                actual,
                candidate => candidate.DeviceBindingId == expectedBinding.DeviceBindingId);
            Assert.Equal(expectedBinding.CapabilityId, binding.CapabilityId);
            Assert.Equal(expectedBinding.DeviceKey, binding.DeviceKey);
        }
    }

    private static void AssertAccepted(EngineeringOperationResult result)
    {
        Assert.True(result.Succeeded, result.Message);
    }

    private static ProjectApplicationWorkspaceScope Scope(string applicationId, string projectDirectory)
    {
        return new ProjectApplicationWorkspaceScope("project.engineering", applicationId, projectDirectory);
    }

    private static string FindDocumentPath(
        string projectDirectory,
        string identityProperty,
        string identityValue)
    {
        var matches = Directory
            .GetFiles(projectDirectory, "*.json", SearchOption.AllDirectories)
            .Where(path => IsDocument(path, identityProperty, identityValue))
            .ToArray();

        return Assert.Single(matches);
    }

    private static bool IsDocument(string path, string identityProperty, string identityValue)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty(identityProperty, out var identity)
                && identity.GetString() == identityValue;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record EngineeringConfigurationFixture(
        Workspace Workspace,
        Recipe Recipe,
        StationProfile StationProfile,
        EngineeringProject Project);

    private string MovedProjectDirectory => $"{_projectDirectory}-moved";
}
