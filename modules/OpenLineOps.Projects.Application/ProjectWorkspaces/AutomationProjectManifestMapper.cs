using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Projects;
using OpenLineOps.Projects.Domain.Snapshots;

namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public static class AutomationProjectManifestMapper
{
    public static AutomationProjectManifest FromProject(AutomationProject project, DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new AutomationProjectManifest(
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectManifest.ProductName,
            project.Id.Value,
            project.DisplayName,
            project.ProjectPath,
            project.CreatedAtUtc,
            updatedAtUtc,
            project.ActiveSnapshotId?.Value,
            project.Applications
                .OrderBy(application => application.Id.Value, StringComparer.Ordinal)
                .Select(ToManifest)
                .ToArray(),
            project.Snapshots
                .OrderBy(snapshot => snapshot.PublishedAtUtc)
                .Select(ToManifest)
                .ToArray());
    }

    public static AutomationProject ToProject(AutomationProjectManifest manifest, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        Validate(manifest);

        var projectId = new AutomationProjectId(manifest.ProjectId);
        var snapshots = manifest.Snapshots
            .Select(snapshot => ToSnapshot(snapshot, projectId))
            .ToArray();
        var activeSnapshotId = string.IsNullOrWhiteSpace(manifest.ActiveSnapshotId)
            ? null
            : new PublishedProjectSnapshotId(manifest.ActiveSnapshotId);

        return AutomationProject.Restore(
            projectId,
            manifest.DisplayName,
            projectPath,
            manifest.CreatedAtUtc,
            activeSnapshotId,
            manifest.Applications.Select(ToApplication),
            snapshots);
    }

    private static void Validate(AutomationProjectManifest manifest)
    {
        if (manifest.FormatVersion != AutomationProjectManifest.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Project manifest format version {manifest.FormatVersion} is not supported.");
        }

        if (!string.Equals(manifest.Product, AutomationProjectManifest.ProductName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Project manifest product '{manifest.Product}' is not supported.");
        }
    }

    private static ProjectApplicationManifest ToManifest(ProjectApplication application)
    {
        return new ProjectApplicationManifest(
            application.Id.Value,
            application.DisplayName,
            application.TopologyId?.Value,
            application.ProcessDefinitionIds
                .Select(processDefinitionId => processDefinitionId.Value)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static PublishedProjectSnapshotManifest ToManifest(PublishedProjectSnapshot snapshot)
    {
        return new PublishedProjectSnapshotManifest(
            snapshot.Id.Value,
            snapshot.ProjectId.Value,
            snapshot.ApplicationId.Value,
            snapshot.TopologyId.Value,
            snapshot.ProcessDefinitionId.Value,
            snapshot.ProcessVersionId.Value,
            snapshot.ConfigurationSnapshotId.Value,
            snapshot.PublishedAtUtc,
            snapshot.CapabilityBindings.Select(ToManifest).ToArray(),
            snapshot.TargetReferences.Select(ToManifest).ToArray(),
            snapshot.BlockVersionIds
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private static SnapshotCapabilityBindingManifest ToManifest(SnapshotCapabilityBinding binding)
    {
        return new SnapshotCapabilityBindingManifest(
            binding.CapabilityId,
            binding.BindingId,
            binding.ProviderKind,
            binding.ProviderKey);
    }

    private static ProjectTargetReferenceManifest ToManifest(ProjectTargetReference target)
    {
        return new ProjectTargetReferenceManifest(target.Kind, target.TargetId);
    }

    private static ProjectApplication ToApplication(ProjectApplicationManifest application)
    {
        return ProjectApplication.Restore(
            new ProjectApplicationId(application.ApplicationId),
            application.DisplayName,
            string.IsNullOrWhiteSpace(application.TopologyId)
                ? null
                : new AutomationTopologyId(application.TopologyId),
            (application.ProcessDefinitionIds ?? [])
                .Select(processDefinitionId => new ProcessDefinitionId(processDefinitionId)));
    }

    private static PublishedProjectSnapshot ToSnapshot(
        PublishedProjectSnapshotManifest snapshot,
        AutomationProjectId expectedProjectId)
    {
        var projectId = new AutomationProjectId(snapshot.ProjectId);
        if (projectId != expectedProjectId)
        {
            throw new InvalidDataException(
                $"Snapshot {snapshot.SnapshotId} references project {projectId}, not {expectedProjectId}.");
        }

        return PublishedProjectSnapshot.Restore(
            new PublishedProjectSnapshotId(snapshot.SnapshotId),
            projectId,
            new ProjectApplicationId(snapshot.ApplicationId),
            new AutomationTopologyId(snapshot.TopologyId),
            new ProcessDefinitionId(snapshot.ProcessDefinitionId),
            new ProcessVersionId(snapshot.ProcessVersionId),
            new ConfigurationSnapshotId(snapshot.ConfigurationSnapshotId),
            (snapshot.CapabilityBindings ?? []).Select(ToCapabilityBinding),
            (snapshot.TargetReferences ?? []).Select(ToTargetReference),
            snapshot.BlockVersionIds ?? [],
            snapshot.PublishedAtUtc);
    }

    private static SnapshotCapabilityBinding ToCapabilityBinding(SnapshotCapabilityBindingManifest binding)
    {
        return new SnapshotCapabilityBinding(
            binding.CapabilityId,
            binding.BindingId,
            binding.ProviderKind,
            binding.ProviderKey);
    }

    private static ProjectTargetReference ToTargetReference(ProjectTargetReferenceManifest target)
    {
        return new ProjectTargetReference(target.Kind, target.TargetId);
    }
}
