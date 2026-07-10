using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Projects;
using OpenLineOps.Projects.Domain.Snapshots;

namespace OpenLineOps.Projects.Application.Projects;

public static class AutomationProjectMapper
{
    public static AutomationProjectDetails ToDetails(AutomationProject project)
    {
        return new AutomationProjectDetails(
            project.Id.Value,
            project.DisplayName,
            project.ProjectPath,
            project.CreatedAtUtc,
            project.ActiveSnapshotId?.Value,
            project.Applications
                .OrderBy(application => application.Id.Value, StringComparer.Ordinal)
                .Select(ToDetails)
                .ToArray(),
            project.Snapshots
                .OrderBy(snapshot => snapshot.PublishedAtUtc)
                .Select(ToDetails)
                .ToArray());
    }

    public static AutomationProjectSummary ToSummary(AutomationProject project)
    {
        return new AutomationProjectSummary(
            project.Id.Value,
            project.DisplayName,
            project.ProjectPath,
            project.ActiveSnapshotId?.Value);
    }

    private static ProjectApplicationDetails ToDetails(ProjectApplication application)
    {
        return new ProjectApplicationDetails(
            application.Id.Value,
            application.DisplayName,
            application.TopologyId?.Value,
            application.ProcessDefinitionIds
                .Select(processId => processId.Value)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            application.ProjectFilePath);
    }

    private static PublishedProjectSnapshotDetails ToDetails(PublishedProjectSnapshot snapshot)
    {
        return new PublishedProjectSnapshotDetails(
            snapshot.Id.Value,
            snapshot.ProjectId.Value,
            snapshot.ApplicationId.Value,
            snapshot.TopologyId.Value,
            snapshot.LayoutIds.Order(StringComparer.Ordinal).ToArray(),
            snapshot.ProductionLineDefinitionId.Value,
            snapshot.PublishedAtUtc,
            snapshot.CapabilityBindings
                .Select(ToDetails)
                .ToArray(),
            snapshot.TargetReferences
                .Select(ToDetails)
                .ToArray(),
            snapshot.BlockVersionIds
                .Order(StringComparer.Ordinal)
                .ToArray(),
            snapshot.ReleaseManifestPath,
            snapshot.ReleaseContentSha256);
    }

    private static SnapshotCapabilityBindingDetails ToDetails(SnapshotCapabilityBinding binding)
    {
        return new SnapshotCapabilityBindingDetails(
            binding.CapabilityId,
            binding.BindingId,
            binding.ProviderKind,
            binding.ProviderKey);
    }

    private static ProjectTargetReferenceDetails ToDetails(ProjectTargetReference target)
    {
        return new ProjectTargetReferenceDetails(target.Kind, target.TargetId);
    }
}
