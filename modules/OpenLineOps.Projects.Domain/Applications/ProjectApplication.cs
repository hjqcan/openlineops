using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Projects.Domain.Identifiers;
using OpenLineOps.Projects.Domain.Operations;

namespace OpenLineOps.Projects.Domain.Applications;

public sealed class ProjectApplication : Entity<ProjectApplicationId>
{
    private readonly List<ProcessDefinitionId> _processDefinitionIds = [];

    private ProjectApplication(ProjectApplicationId id, string displayName)
        : base(id)
    {
        DisplayName = ProjectIdGuard.NotBlank(displayName, nameof(displayName));
    }

    public string DisplayName { get; }

    public AutomationTopologyId? TopologyId { get; private set; }

    public IReadOnlyCollection<ProcessDefinitionId> ProcessDefinitionIds => _processDefinitionIds.AsReadOnly();

    public static ProjectApplication Create(ProjectApplicationId id, string displayName)
    {
        return new ProjectApplication(id, displayName);
    }

    public static ProjectApplication Restore(
        ProjectApplicationId id,
        string displayName,
        AutomationTopologyId? topologyId,
        IEnumerable<ProcessDefinitionId> processDefinitionIds)
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

        var application = new ProjectApplication(id, displayName)
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
}
