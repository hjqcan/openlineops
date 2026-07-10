using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;

namespace OpenLineOps.Production.Domain.Aggregates;

public sealed class ProductionLineDefinition : AggregateRoot<ProductionLineDefinitionId>
{
    private readonly List<WorkstationDefinition> _workstations;
    private readonly List<ProcessStage> _stages;
    private readonly List<ExternalTestProgramAdapter> _externalTestProgramAdapters;

    private ProductionLineDefinition(
        ProductionLineDefinitionId id,
        string displayName,
        string topologyId,
        DutModelDefinition dutModel,
        IEnumerable<WorkstationDefinition> workstations,
        IEnumerable<ProcessStage> stages,
        IEnumerable<ExternalTestProgramAdapter> externalTestProgramAdapters,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        DisplayName = ProductionIdGuard.NotBlank(displayName, nameof(displayName));
        TopologyId = ProductionIdGuard.NotBlank(topologyId, nameof(topologyId));
        DutModel = dutModel ?? throw new ArgumentNullException(nameof(dutModel));
        _workstations = MaterializeRequired(workstations, nameof(workstations));
        _stages = MaterializeRequired(stages, nameof(stages))
            .OrderBy(stage => stage.Sequence)
            .ToList();
        _externalTestProgramAdapters = MaterializeRequired(
            externalTestProgramAdapters,
            nameof(externalTestProgramAdapters));
        EnsureValidComposition();
        if (createdAtUtc.Offset != TimeSpan.Zero || updatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Production line timestamps must use UTC offset zero.");
        }

        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Production line updated timestamp cannot precede creation.");
        }

        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public string DisplayName { get; }

    public string TopologyId { get; }

    public DutModelDefinition DutModel { get; }

    public IReadOnlyCollection<WorkstationDefinition> Workstations => _workstations.AsReadOnly();

    public IReadOnlyCollection<ProcessStage> Stages => _stages.AsReadOnly();

    public IReadOnlyCollection<ExternalTestProgramAdapter> ExternalTestProgramAdapters =>
        _externalTestProgramAdapters.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public static ProductionLineDefinition Create(
        ProductionLineDefinitionId id,
        string displayName,
        string topologyId,
        DutModelDefinition dutModel,
        IEnumerable<WorkstationDefinition> workstations,
        IEnumerable<ProcessStage> stages,
        IEnumerable<ExternalTestProgramAdapter> externalTestProgramAdapters,
        DateTimeOffset createdAtUtc)
    {
        return Restore(
            id,
            displayName,
            topologyId,
            dutModel,
            workstations,
            stages,
            externalTestProgramAdapters,
            createdAtUtc,
            createdAtUtc);
    }

    public static ProductionLineDefinition Restore(
        ProductionLineDefinitionId id,
        string displayName,
        string topologyId,
        DutModelDefinition dutModel,
        IEnumerable<WorkstationDefinition> workstations,
        IEnumerable<ProcessStage> stages,
        IEnumerable<ExternalTestProgramAdapter> externalTestProgramAdapters,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(workstations);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(externalTestProgramAdapters);

        return new ProductionLineDefinition(
            id,
            displayName,
            topologyId,
            dutModel,
            workstations,
            stages,
            externalTestProgramAdapters,
            createdAtUtc,
            updatedAtUtc);
    }

    private void EnsureValidComposition()
    {
        if (_workstations.Count == 0 || _stages.Count == 0)
        {
            throw new ArgumentException("Production line requires at least one workstation and one stage.");
        }

        EnsureUnique(_workstations.Select(workstation => workstation.Id.Value), "workstation ids");
        EnsureUnique(
            _workstations.Select(workstation =>
                $"{workstation.TopologyStationNodeId}\u001f{workstation.TopologySystemModuleId}"),
            "workstation topology station/system bindings");
        EnsureUnique(_stages.Select(stage => stage.Id.Value), "stage ids");
        if (_stages.Select(stage => stage.Sequence).Distinct().Count() != _stages.Count)
        {
            throw new ArgumentException("Production line stage sequences must be unique.");
        }
        EnsureUnique(
            _externalTestProgramAdapters.Select(adapter => adapter.Id.Value),
            "external test program adapter ids");

        for (var index = 0; index < _stages.Count; index++)
        {
            var stage = _stages[index];
            if (stage.Sequence != index + 1)
            {
                throw new ArgumentException("Process stage sequence must be contiguous and start at 1.");
            }

            if (_workstations.All(workstation => workstation.Id != stage.WorkstationId))
            {
                throw new ArgumentException(
                    $"Process stage {stage.Id} references missing workstation {stage.WorkstationId}.");
            }

            if (stage.ExternalTestProgramAdapterId is not null
                && _externalTestProgramAdapters.All(adapter =>
                    adapter.Id != stage.ExternalTestProgramAdapterId))
            {
                throw new ArgumentException(
                    $"Process stage {stage.Id} references missing external test program adapter {stage.ExternalTestProgramAdapterId}.");
            }
        }

        var unusedWorkstation = _workstations.FirstOrDefault(workstation =>
            _stages.All(stage => stage.WorkstationId != workstation.Id));
        if (unusedWorkstation is not null)
        {
            throw new ArgumentException(
                $"Workstation {unusedWorkstation.Id} must be used by at least one process stage.");
        }

        var unusedAdapter = _externalTestProgramAdapters.FirstOrDefault(adapter =>
            _stages.All(stage => stage.ExternalTestProgramAdapterId != adapter.Id));
        if (unusedAdapter is not null)
        {
            throw new ArgumentException(
                $"External test program adapter {unusedAdapter.Id} must be used by at least one process stage.");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var valueArray = values.ToArray();
        if (valueArray.Distinct(StringComparer.OrdinalIgnoreCase).Count() != valueArray.Length)
        {
            throw new ArgumentException($"Production line {description} must be unique ignoring case.");
        }
    }

    private static List<T> MaterializeRequired<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        var items = values.ToList();
        if (items.Any(static item => item is null))
        {
            throw new ArgumentException(
                "Production line semantic collections cannot contain null items.",
                parameterName);
        }

        return items;
    }
}
