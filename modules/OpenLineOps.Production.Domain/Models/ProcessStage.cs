using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Domain.Models;

public sealed class ProcessStage : Entity<ProcessStageId>
{
    private ProcessStage(
        ProcessStageId id,
        int sequence,
        string displayName,
        WorkstationId workstationId,
        string flowDefinitionId,
        ExternalTestProgramAdapterId? externalTestProgramAdapterId)
        : base(id ?? throw new ArgumentNullException(nameof(id)))
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Process stage sequence must be positive.");
        }

        Sequence = sequence;
        DisplayName = ProductionIdGuard.NotBlank(displayName, nameof(displayName));
        WorkstationId = workstationId ?? throw new ArgumentNullException(nameof(workstationId));
        FlowDefinitionId = ProductionIdGuard.NotBlank(flowDefinitionId, nameof(flowDefinitionId));
        ExternalTestProgramAdapterId = externalTestProgramAdapterId;
    }

    public int Sequence { get; }

    public string DisplayName { get; }

    public WorkstationId WorkstationId { get; }

    public string FlowDefinitionId { get; }

    public ExternalTestProgramAdapterId? ExternalTestProgramAdapterId { get; }

    public static ProcessStage Create(
        ProcessStageId id,
        int sequence,
        string displayName,
        WorkstationId workstationId,
        string flowDefinitionId,
        ExternalTestProgramAdapterId? externalTestProgramAdapterId = null)
    {
        return new ProcessStage(
            id,
            sequence,
            displayName,
            workstationId,
            flowDefinitionId,
            externalTestProgramAdapterId);
    }
}
