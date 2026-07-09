using OpenLineOps.Engineering.Domain.Events;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Operations;
using OpenLineOps.Engineering.Domain.Projects;
using OpenLineOps.Engineering.Domain.Recipes;
using OpenLineOps.Engineering.Domain.Stations;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Tests;

public sealed class EngineeringConfigurationTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 6, 29, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PublishedAtUtc = CreatedAtUtc.AddMinutes(10);

    [Fact]
    public void WorkspaceCapturesIdentityAndDisplayName()
    {
        var workspace = Workspace.Create(
            new WorkspaceId("workspace-main"),
            "Main Manufacturing Workspace",
            CreatedAtUtc);

        Assert.Equal("workspace-main", workspace.Id.Value);
        Assert.Equal("Main Manufacturing Workspace", workspace.DisplayName);
        Assert.Equal(CreatedAtUtc, workspace.CreatedAtUtc);
    }

    [Fact]
    public void RecipeVersionIsImmutableAfterPublish()
    {
        var recipe = CreateDraftRecipe();
        var addBeforePublish = recipe.AddOrUpdateParameter("voltage.max", "5.2");
        var publishResult = recipe.Publish(PublishedAtUtc);

        var addAfterPublish = recipe.AddOrUpdateParameter("voltage.max", "5.4");

        Assert.True(addBeforePublish.Succeeded);
        Assert.True(publishResult.Succeeded);
        Assert.False(addAfterPublish.Succeeded);
        Assert.Equal("Engineering.RecipeImmutable", addAfterPublish.Code);
        Assert.Equal(RecipeStatus.Published, recipe.Status);
        Assert.Equal("5.2", Assert.Single(recipe.Parameters).Value);

        var domainEvent = Assert.IsType<RecipePublishedDomainEvent>(Assert.Single(recipe.DomainEvents));
        Assert.Equal(recipe.Id, domainEvent.RecipeId);
        Assert.Equal(recipe.VersionId, domainEvent.VersionId);
    }

    [Fact]
    public void DraftRecipeCannotBePublishedIntoConfigurationSnapshot()
    {
        var project = CreateProject();
        var draftRecipe = CreateDraftRecipe();
        var stationProfile = CreateStationProfile();

        var result = project.PublishSnapshot(
            SnapshotId("snapshot-draft-recipe"),
            ProcessDefinitionId("process-packaging"),
            ProcessVersionId("process-packaging@1.0.0"),
            draftRecipe,
            stationProfile,
            PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Engineering.RecipeNotPublished", result.Code);
        Assert.Empty(project.Snapshots);
        Assert.Null(project.ActiveSnapshotId);
    }

    [Fact]
    public void PublishedSnapshotContainsTraceableEngineeringInputs()
    {
        var project = CreateProject();
        var recipe = CreatePublishedRecipe();
        var stationProfile = CreateStationProfile();

        var result = project.PublishSnapshot(
            SnapshotId("snapshot-001"),
            ProcessDefinitionId("process-packaging"),
            ProcessVersionId("process-packaging@1.0.0"),
            recipe,
            stationProfile,
            PublishedAtUtc);

        var snapshot = Assert.Single(project.Snapshots);
        var deviceBinding = Assert.Single(snapshot.DeviceBindings);

        Assert.True(result.Succeeded);
        Assert.Equal(snapshot.Id, project.ActiveSnapshotId);
        Assert.Equal(project.Id, snapshot.ProjectId);
        Assert.Equal("process-packaging", snapshot.ProcessDefinitionId.Value);
        Assert.Equal("process-packaging@1.0.0", snapshot.ProcessVersionId.Value);
        Assert.Equal(recipe.Id, snapshot.RecipeId);
        Assert.Equal(recipe.VersionId, snapshot.RecipeVersionId);
        Assert.Equal(stationProfile.Id, snapshot.StationProfileId);
        Assert.Equal("device.scanner", deviceBinding.CapabilityId.Value);
        Assert.Equal("scanner-01", deviceBinding.DeviceKey);
        Assert.True(snapshot.IsPublished);

        var domainEvent = Assert.IsType<ConfigurationSnapshotPublishedDomainEvent>(
            Assert.Single(project.DomainEvents));
        Assert.Equal(project.Id, domainEvent.ProjectId);
        Assert.Equal(snapshot.Id, domainEvent.SnapshotId);
    }

    [Fact]
    public void RollbackToPublishedSnapshotChangesActiveSnapshot()
    {
        var project = CreateProject();
        var recipe = CreatePublishedRecipe();
        var stationProfile = CreateStationProfile();
        PublishSnapshot(project, "snapshot-001", recipe, stationProfile);
        PublishSnapshot(project, "snapshot-002", recipe, stationProfile);

        var result = project.RollbackToSnapshot(SnapshotId("snapshot-001"), PublishedAtUtc.AddMinutes(5));

        Assert.True(result.Succeeded);
        Assert.Equal("snapshot-001", project.ActiveSnapshotId?.Value);
        Assert.IsType<EngineeringProjectRolledBackDomainEvent>(project.DomainEvents.Last());
    }

    [Fact]
    public void RollbackToUnknownSnapshotIsRejected()
    {
        var project = CreateProject();

        var result = project.RollbackToSnapshot(SnapshotId("missing"), PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Engineering.SnapshotNotFound", result.Code);
        Assert.Null(project.ActiveSnapshotId);
    }

    [Fact]
    public void StationProfileRejectsDuplicateCapabilityBinding()
    {
        var stationProfile = CreateStationProfile();

        var result = stationProfile.AddDeviceBinding(DeviceBinding.Create(
            DeviceBindingId("scanner-secondary"),
            DeviceCapabilityId("device.scanner"),
            "scanner-02"));

        Assert.False(result.Succeeded);
        Assert.Equal("Engineering.CapabilityAlreadyBound", result.Code);
        Assert.Single(stationProfile.DeviceBindings);
    }

    [Fact]
    public void SnapshotRequiresStationDeviceBindings()
    {
        var project = CreateProject();
        var recipe = CreatePublishedRecipe();
        var stationProfile = StationProfile.Create(StationProfileId("station-empty"), "Empty Station");

        var result = project.PublishSnapshot(
            SnapshotId("snapshot-no-devices"),
            ProcessDefinitionId("process-packaging"),
            ProcessVersionId("process-packaging@1.0.0"),
            recipe,
            stationProfile,
            PublishedAtUtc);

        Assert.False(result.Succeeded);
        Assert.Equal("Engineering.StationHasNoDeviceBindings", result.Code);
        Assert.Empty(project.Snapshots);
    }

    private static EngineeringProject CreateProject()
    {
        return EngineeringProject.Create(
            EngineeringProjectId("project-packaging"),
            WorkspaceId("workspace-main"),
            "Packaging Line Project",
            CreatedAtUtc);
    }

    private static Recipe CreateDraftRecipe()
    {
        var recipe = Recipe.Create(
            RecipeId("recipe-eol"),
            RecipeVersionId("recipe-eol@1.0.0"),
            "End Of Line Recipe",
            CreatedAtUtc);

        AssertAccepted(recipe.AddOrUpdateParameter("voltage.max", "5.2"));

        return recipe;
    }

    private static Recipe CreatePublishedRecipe()
    {
        var recipe = CreateDraftRecipe();

        AssertAccepted(recipe.Publish(PublishedAtUtc));
        recipe.ClearDomainEvents();

        return recipe;
    }

    private static StationProfile CreateStationProfile()
    {
        var stationProfile = StationProfile.Create(
            StationProfileId("station-eol"),
            "End Of Line Station");

        AssertAccepted(stationProfile.AddDeviceBinding(DeviceBinding.Create(
            DeviceBindingId("scanner-primary"),
            DeviceCapabilityId("device.scanner"),
            "scanner-01")));

        return stationProfile;
    }

    private static void PublishSnapshot(
        EngineeringProject project,
        string snapshotId,
        Recipe recipe,
        StationProfile stationProfile)
    {
        AssertAccepted(project.PublishSnapshot(
            SnapshotId(snapshotId),
            ProcessDefinitionId("process-packaging"),
            ProcessVersionId("process-packaging@1.0.0"),
            recipe,
            stationProfile,
            PublishedAtUtc));
    }

    private static void AssertAccepted(EngineeringOperationResult result)
    {
        Assert.True(result.Succeeded, result.Message);
    }

    private static ConfigurationSnapshotId SnapshotId(string value)
    {
        return new ConfigurationSnapshotId(value);
    }

    private static DeviceBindingId DeviceBindingId(string value)
    {
        return new DeviceBindingId(value);
    }

    private static DeviceCapabilityId DeviceCapabilityId(string value)
    {
        return new DeviceCapabilityId(value);
    }

    private static EngineeringProjectId EngineeringProjectId(string value)
    {
        return new EngineeringProjectId(value);
    }

    private static ProcessDefinitionId ProcessDefinitionId(string value)
    {
        return new ProcessDefinitionId(value);
    }

    private static ProcessVersionId ProcessVersionId(string value)
    {
        return new ProcessVersionId(value);
    }

    private static RecipeId RecipeId(string value)
    {
        return new RecipeId(value);
    }

    private static RecipeVersionId RecipeVersionId(string value)
    {
        return new RecipeVersionId(value);
    }

    private static StationProfileId StationProfileId(string value)
    {
        return new StationProfileId(value);
    }

    private static WorkspaceId WorkspaceId(string value)
    {
        return new WorkspaceId(value);
    }
}
