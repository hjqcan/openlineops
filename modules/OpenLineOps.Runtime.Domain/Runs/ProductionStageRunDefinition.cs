using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed record ProductionStageRunDefinition
{
    public ProductionStageRunDefinition(
        string stageId,
        int sequence,
        string workstationId,
        StationId stationId,
        ProcessDefinitionId processDefinitionId,
        ProcessVersionId processVersionId,
        ConfigurationSnapshotId configurationSnapshotId,
        RecipeSnapshotId recipeSnapshotId)
    {
        StageId = ProductionRunText.Required(stageId, nameof(stageId));
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Stage sequence must be positive.");
        }

        Sequence = sequence;
        WorkstationId = ProductionRunText.Required(workstationId, nameof(workstationId));
        StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
        ProcessDefinitionId = processDefinitionId
            ?? throw new ArgumentNullException(nameof(processDefinitionId));
        ProcessVersionId = processVersionId ?? throw new ArgumentNullException(nameof(processVersionId));
        ConfigurationSnapshotId = configurationSnapshotId
            ?? throw new ArgumentNullException(nameof(configurationSnapshotId));
        RecipeSnapshotId = recipeSnapshotId ?? throw new ArgumentNullException(nameof(recipeSnapshotId));
    }

    public string StageId { get; }

    public int Sequence { get; }

    public string WorkstationId { get; }

    public StationId StationId { get; }

    public ProcessDefinitionId ProcessDefinitionId { get; }

    public ProcessVersionId ProcessVersionId { get; }

    public ConfigurationSnapshotId ConfigurationSnapshotId { get; }

    public RecipeSnapshotId RecipeSnapshotId { get; }
}
