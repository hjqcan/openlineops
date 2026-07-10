using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Operations;

namespace OpenLineOps.Projects.Domain.Applications;

public sealed class ProjectApplication : Entity<ProjectApplicationId>
{
    private readonly List<ProcessDefinitionId> _processDefinitionIds = [];

    private ProjectApplication(
        ProjectApplicationId id,
        string displayName,
        string projectFilePath)
        : base(id)
    {
        DisplayName = ProjectIdGuard.NotBlank(displayName, nameof(displayName));
        ProjectFilePath = NormalizeProjectFilePath(projectFilePath);
    }

    public string DisplayName { get; }

    public string ProjectFilePath { get; }

    public AutomationTopologyId? TopologyId { get; private set; }

    public IReadOnlyCollection<ProcessDefinitionId> ProcessDefinitionIds => _processDefinitionIds.AsReadOnly();

    public static ProjectApplication Create(
        ProjectApplicationId id,
        string displayName,
        string projectFilePath)
    {
        return new ProjectApplication(id, displayName, projectFilePath);
    }

    public static ProjectApplication Restore(
        ProjectApplicationId id,
        string displayName,
        AutomationTopologyId? topologyId,
        IEnumerable<ProcessDefinitionId> processDefinitionIds,
        string projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(processDefinitionIds);

        var processDefinitionIdList = processDefinitionIds.ToArray();
        var distinctProcessDefinitionIds = processDefinitionIdList
            .Distinct()
            .ToArray();

        if (distinctProcessDefinitionIds.Length != processDefinitionIdList.Length)
        {
            throw new ArgumentException("Process definition ids must be unique.", nameof(processDefinitionIds));
        }

        var application = new ProjectApplication(id, displayName, projectFilePath)
        {
            TopologyId = topologyId
        };

        application._processDefinitionIds.AddRange(distinctProcessDefinitionIds);

        return application;
    }

    public ProjectOperationResult LinkTopology(AutomationTopologyId topologyId)
    {
        TopologyId = topologyId;

        return ProjectOperationResult.Accepted("Topology linked.");
    }

    public ProjectOperationResult AddProcessDefinition(ProcessDefinitionId processDefinitionId)
    {
        if (_processDefinitionIds.Contains(processDefinitionId))
        {
            return ProjectOperationResult.Rejected(
                "Projects.ProcessAlreadyLinked",
                $"Process definition {processDefinitionId} is already linked to application {Id}.");
        }

        _processDefinitionIds.Add(processDefinitionId);

        return ProjectOperationResult.Accepted("Process definition linked.");
    }

    private static string NormalizeProjectFilePath(string projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath)
            || Path.IsPathRooted(projectFilePath)
            || projectFilePath.Contains('\\')
            || !string.Equals(projectFilePath, projectFilePath.Trim(), StringComparison.Ordinal)
            || !projectFilePath.EndsWith(".oloapp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Application project file path must be a forward-slash relative .oloapp path.",
                nameof(projectFilePath));
        }

        var segments = projectFilePath.Split('/');
        if (segments.Length != 3
            || !string.Equals(segments[0], "applications", StringComparison.Ordinal)
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException(
                "Application project file path must be applications/<folder>/<name>.oloapp.",
                nameof(projectFilePath));
        }

        return projectFilePath;
    }
}
