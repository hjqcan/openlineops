using OpenLineOps.Production.Domain.Aggregates;

namespace OpenLineOps.Production.Application.LineDefinitions;

public static class ProductionLineDefinitionMapper
{
    public static ProductionLineDefinitionDetails ToDetails(ProductionLineDefinition definition)
    {
        var orderedStages = definition.Stages.OrderBy(stage => stage.Sequence).ToArray();
        return new ProductionLineDefinitionDetails(
            definition.Id.Value,
            definition.DisplayName,
            definition.TopologyId,
            new DutModelDetails(
                definition.DutModel.Id.Value,
                definition.DutModel.ModelCode,
                definition.DutModel.IdentityInputKey),
            definition.Workstations
                .OrderBy(workstation => workstation.Id.Value, StringComparer.Ordinal)
                .Select(workstation => new WorkstationDetails(
                    workstation.Id.Value,
                    workstation.DisplayName,
                    workstation.StationSystemId))
                .ToArray(),
            orderedStages
                .Select((stage, index) => new ProcessStageDetails(
                    stage.Id.Value,
                    stage.Sequence,
                    stage.DisplayName,
                    stage.WorkstationId.Value,
                    stage.FlowDefinitionId,
                    stage.ConfigurationSnapshotId,
                    stage.ExternalTestProgramAdapterId?.Value,
                    index + 1 < orderedStages.Length ? orderedStages[index + 1].Id.Value : null))
                .ToArray(),
            definition.ExternalTestProgramAdapters
                .OrderBy(adapter => adapter.Id.Value, StringComparer.Ordinal)
                .Select(adapter => new ExternalTestProgramAdapterDetails(
                    adapter.Id.Value,
                    adapter.DisplayName,
                    adapter.CapabilityId,
                    adapter.CommandName,
                    adapter.LaunchKind.ToString(),
                    adapter.Executable,
                    adapter.ProviderKey,
                    adapter.ArgumentTemplates.ToArray(),
                    adapter.InputMappings.Select(mapping =>
                        new ExternalTestProgramInputMappingDetails(mapping.Source, mapping.Target)).ToArray(),
                    adapter.ResultMappings.Select(mapping =>
                        new ExternalTestProgramResultMappingDetails(mapping.SourcePath, mapping.TargetKey)).ToArray(),
                    new ExternalTestProgramOutcomeMappingDetails(
                        adapter.OutcomeMapping.SourcePath,
                        adapter.OutcomeMapping.PassedToken,
                        adapter.OutcomeMapping.FailedToken,
                        adapter.OutcomeMapping.AbortedToken),
                    checked(adapter.Timeout.Ticks / TimeSpan.TicksPerMillisecond)))
                .ToArray(),
            definition.CreatedAtUtc,
            definition.UpdatedAtUtc);
    }

    public static ProductionLineDefinitionSummary ToSummary(ProductionLineDefinition definition)
    {
        return new ProductionLineDefinitionSummary(
            definition.Id.Value,
            definition.DisplayName,
            definition.TopologyId,
            definition.DutModel.ModelCode,
            definition.Stages.Count,
            definition.UpdatedAtUtc);
    }
}
