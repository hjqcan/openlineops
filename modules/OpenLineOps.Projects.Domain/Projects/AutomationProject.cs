using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Projects.Domain.Applications;
using OpenLineOps.Projects.Domain.Events;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Operations;
using OpenLineOps.Projects.Domain.Snapshots;

namespace OpenLineOps.Projects.Domain.Projects;

public sealed class AutomationProject : AggregateRoot<AutomationProjectId>
{
    private readonly List<ProjectApplication> _applications = [];
    private readonly List<PublishedProjectSnapshot> _snapshots = [];

    private AutomationProject(
        AutomationProjectId id,
        string displayName,
        string projectPath,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        DisplayName = ProjectIdGuard.NotBlank(displayName, nameof(displayName));
        ProjectPath = ProjectIdGuard.NotBlank(projectPath, nameof(projectPath));
        CreatedAtUtc = createdAtUtc;
    }

    public string DisplayName { get; }

    public string ProjectPath { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public PublishedProjectSnapshotId? ActiveSnapshotId { get; private set; }

    public IReadOnlyCollection<ProjectApplication> Applications => _applications.AsReadOnly();

    public IReadOnlyCollection<PublishedProjectSnapshot> Snapshots => _snapshots.AsReadOnly();

    public static AutomationProject Create(
        AutomationProjectId id,
        string displayName,
        string projectPath,
        DateTimeOffset createdAtUtc)
    {
        return new AutomationProject(id, displayName, projectPath, createdAtUtc);
    }

    public static AutomationProject Restore(
        AutomationProjectId id,
        string displayName,
        string projectPath,
        DateTimeOffset createdAtUtc,
        PublishedProjectSnapshotId? activeSnapshotId,
        IEnumerable<ProjectApplication> applications,
        IEnumerable<PublishedProjectSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(snapshots);

        var applicationList = applications.ToArray();
        var snapshotList = snapshots.ToArray();

        if (applicationList.Select(application => application.Id).Distinct().Count() != applicationList.Length)
        {
            throw new ArgumentException("Application ids must be unique.", nameof(applications));
        }

        var applicationProjectPaths = applicationList
            .Select(application => application.ProjectFilePath)
            .ToArray();
        if (applicationProjectPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != applicationProjectPaths.Length)
        {
            throw new ArgumentException(
                "Application project file paths must be unique ignoring case.",
                nameof(applications));
        }

        if (snapshotList.Select(snapshot => snapshot.Id).Distinct().Count() != snapshotList.Length)
        {
            throw new ArgumentException("Snapshot ids must be unique.", nameof(snapshots));
        }

        if (activeSnapshotId is not null && !snapshotList.Any(snapshot => snapshot.Id == activeSnapshotId))
        {
            throw new ArgumentException("Active snapshot id must reference a restored snapshot.", nameof(activeSnapshotId));
        }

        var project = new AutomationProject(id, displayName, projectPath, createdAtUtc)
        {
            ActiveSnapshotId = activeSnapshotId
        };

        project._applications.AddRange(applicationList);
        project._snapshots.AddRange(snapshotList);

        return project;
    }

    public ProjectOperationResult AddApplication(ProjectApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (_applications.Any(candidate => candidate.Id == application.Id))
        {
            return ProjectOperationResult.Rejected(
                "Projects.ApplicationAlreadyExists",
                $"Application {application.Id} already exists in project {Id}.");
        }

        if (_applications.Any(candidate => string.Equals(
            candidate.DisplayName,
            application.DisplayName,
            StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectOperationResult.Rejected(
                "Projects.ApplicationNameAlreadyExists",
                $"Application name {application.DisplayName} already exists in project {Id}.");
        }

        if (_applications.Any(candidate => string.Equals(
                candidate.ProjectFilePath,
                application.ProjectFilePath,
                StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectOperationResult.Rejected(
                "Projects.ApplicationProjectPathAlreadyExists",
                $"Application project path {application.ProjectFilePath} already exists in project {Id}.");
        }

        _applications.Add(application);

        return ProjectOperationResult.Accepted("Application added.");
    }

    public ProjectOperationResult LinkTopology(ProjectApplicationId applicationId, AutomationTopologyId topologyId)
    {
        var application = FindApplication(applicationId);
        if (application is null)
        {
            return ProjectOperationResult.Rejected(
                "Projects.ApplicationMissing",
                $"Application {applicationId} was not found in project {Id}.");
        }

        return application.LinkTopology(topologyId);
    }

    public ProjectOperationResult LinkProcessDefinition(
        ProjectApplicationId applicationId,
        ProcessDefinitionId processDefinitionId)
    {
        var application = FindApplication(applicationId);
        if (application is null)
        {
            return ProjectOperationResult.Rejected(
                "Projects.ApplicationMissing",
                $"Application {applicationId} was not found in project {Id}.");
        }

        return application.AddProcessDefinition(processDefinitionId);
    }

    public ProjectOperationResult PublishSnapshot(
        PublishedProjectSnapshotId snapshotId,
        ProjectApplicationId applicationId,
        AutomationTopologyId topologyId,
        IEnumerable<string> layoutIds,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        IEnumerable<SnapshotCapabilityBinding> capabilityBindings,
        IEnumerable<ProjectTargetReference> targetReferences,
        IEnumerable<string> blockVersionIds,
        string releaseManifestPath,
        string releaseContentSha256,
        DateTimeOffset publishedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(layoutIds);
        ArgumentNullException.ThrowIfNull(capabilityBindings);
        ArgumentNullException.ThrowIfNull(targetReferences);
        ArgumentNullException.ThrowIfNull(blockVersionIds);

        if (_snapshots.Any(candidate => candidate.Id == snapshotId))
        {
            return ProjectOperationResult.Rejected(
                "Projects.SnapshotAlreadyExists",
                $"Project snapshot {snapshotId} already exists in project {Id}.");
        }

        var application = FindApplication(applicationId);
        if (application is null)
        {
            return ProjectOperationResult.Rejected(
                "Projects.ApplicationMissing",
                $"Application {applicationId} was not found in project {Id}.");
        }

        if (application.TopologyId != topologyId)
        {
            return ProjectOperationResult.Rejected(
                "Projects.TopologyNotLinked",
                $"Topology {topologyId} is not linked to application {applicationId}.");
        }

        if (!application.ProcessDefinitionIds.Contains(processDefinitionId))
        {
            return ProjectOperationResult.Rejected(
                "Projects.ProcessNotLinked",
                $"Process definition {processDefinitionId} is not linked to application {applicationId}.");
        }

        var layouts = layoutIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (layouts.Length == 0)
        {
            return ProjectOperationResult.Rejected(
                "Projects.NoLayouts",
                "A project snapshot requires at least one frozen site layout.");
        }

        var bindings = capabilityBindings.ToList();
        if (bindings.Count == 0)
        {
            return ProjectOperationResult.Rejected(
                "Projects.NoCapabilityBindings",
                "A project snapshot requires at least one resolved capability binding.");
        }

        var targets = targetReferences.ToList();
        if (targets.Count == 0)
        {
            return ProjectOperationResult.Rejected(
                "Projects.NoRuntimeTargets",
                "A project snapshot requires at least one runtime target reference.");
        }

        var snapshot = PublishedProjectSnapshot.Publish(
            snapshotId,
            Id,
            applicationId,
            topologyId,
            layouts,
            processDefinitionId,
            processVersionId,
            configurationSnapshotId,
            bindings,
            targets,
            blockVersionIds,
            releaseManifestPath,
            releaseContentSha256,
            publishedAtUtc);

        _snapshots.Add(snapshot);
        ActiveSnapshotId = snapshot.Id;

        RaiseDomainEvent(new ProjectSnapshotPublishedDomainEvent(Id, snapshot.Id, applicationId, topologyId, publishedAtUtc));

        return ProjectOperationResult.Accepted("Project snapshot published.");
    }

    private ProjectApplication? FindApplication(ProjectApplicationId applicationId)
    {
        return _applications.SingleOrDefault(candidate => candidate.Id == applicationId);
    }
}
