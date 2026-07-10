using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Processes.Domain.Identifiers;
using OpenLineOps.Processes.Domain.Nodes;
using OpenLineOps.Processes.Domain.Operations;
using OpenLineOps.Processes.Domain.Transitions;
using OpenLineOps.Processes.Domain.Validation;

namespace OpenLineOps.Processes.Domain.Definitions;

public sealed class ProcessDefinition : AggregateRoot<ProcessDefinitionId>
{
    private readonly List<ProcessNode> _nodes = [];
    private readonly List<ProcessTransition> _transitions = [];

    private ProcessDefinition(
        ProcessDefinitionId id,
        ProcessVersionId versionId,
        string displayName,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        VersionId = versionId;
        DisplayName = ProcessIdGuard.NotBlank(displayName, nameof(displayName));
        CreatedAtUtc = createdAtUtc;
        Status = ProcessDefinitionStatus.Draft;
    }

    public ProcessVersionId VersionId { get; }

    public string DisplayName { get; }

    public ProcessDefinitionStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public IReadOnlyCollection<ProcessNode> Nodes => _nodes.AsReadOnly();

    public IReadOnlyCollection<ProcessTransition> Transitions => _transitions.AsReadOnly();

    public bool IsPublished => Status == ProcessDefinitionStatus.Published;

    public static ProcessDefinition Create(
        ProcessDefinitionId id,
        ProcessVersionId versionId,
        string displayName,
        DateTimeOffset createdAtUtc)
    {
        return new ProcessDefinition(id, versionId, displayName, createdAtUtc);
    }

    public ProcessOperationResult AddNode(ProcessNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var immutableResult = EnsureDraft();
        if (!immutableResult.Succeeded)
        {
            return immutableResult;
        }

        if (_nodes.Any(candidate => candidate.Id == node.Id))
        {
            return ProcessOperationResult.Rejected(
                "Processes.NodeAlreadyExists",
                $"Process node {node.Id} already exists.");
        }

        _nodes.Add(node);

        return ProcessOperationResult.Accepted("Process node added.");
    }

    public ProcessOperationResult AddTransition(ProcessTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        var immutableResult = EnsureDraft();
        if (!immutableResult.Succeeded)
        {
            return immutableResult;
        }

        if (_transitions.Any(candidate => candidate.Id == transition.Id))
        {
            return ProcessOperationResult.Rejected(
                "Processes.TransitionAlreadyExists",
                $"Process transition {transition.Id} already exists.");
        }

        _transitions.Add(transition);

        return ProcessOperationResult.Accepted("Process transition added.");
    }

    public ProcessOperationResult Publish(DateTimeOffset publishedAtUtc)
    {
        if (Status == ProcessDefinitionStatus.Published)
        {
            return ProcessOperationResult.Accepted("Process definition is already published.");
        }

        var validationReport = ProcessGraphValidator.Validate(this);
        if (!validationReport.IsValid)
        {
            return ProcessOperationResult.Rejected(
                "Processes.PublishValidationFailed",
                $"Process definition {Id} cannot be published because graph validation failed.");
        }

        Status = ProcessDefinitionStatus.Published;
        PublishedAtUtc = publishedAtUtc;

        return ProcessOperationResult.Accepted("Process definition published.");
    }

    private ProcessOperationResult EnsureDraft()
    {
        if (Status != ProcessDefinitionStatus.Draft)
        {
            return ProcessOperationResult.Rejected(
                "Processes.DefinitionImmutable",
                $"Process definition {Id} cannot be changed after publication.");
        }

        return ProcessOperationResult.Accepted();
    }
}
