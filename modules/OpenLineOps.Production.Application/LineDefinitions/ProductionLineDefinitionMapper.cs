using OpenLineOps.Production.Domain.Aggregates;

namespace OpenLineOps.Production.Application.LineDefinitions;

public static class ProductionLineDefinitionMapper
{
    public static ProductionLineDefinitionDetails ToDetails(ProductionLineDefinition definition)
    {
        return new ProductionLineDefinitionDetails(
            definition.Id.Value,
            definition.DisplayName,
            definition.TopologyId,
            new ProductModelDetails(
                definition.ProductModel.Id.Value,
                definition.ProductModel.ModelCode,
                definition.ProductModel.IdentityInputKey),
            definition.EntryOperationId.Value,
            definition.Operations
                .OrderBy(operation => operation.Id.Value, StringComparer.Ordinal)
                .Select(operation => new OperationDefinitionDetails(
                    operation.Id.Value,
                    operation.DisplayName,
                    operation.StationSystemId,
                    operation.FlowDefinitionId,
                    operation.ConfigurationSnapshotId))
                .ToArray(),
            definition.Transitions
                .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
                .Select(transition => new RouteTransitionDetails(
                    transition.Id.Value,
                    transition.SourceOperationId.Value,
                    transition.TargetOperationId.Value,
                    transition.Kind.ToString(),
                    transition.RequiredJudgement?.ToString(),
                    transition.MaxTraversals,
                    transition.ParallelGroupId,
                    transition.OutputCondition?.OutputKey,
                    transition.OutputCondition?.ExpectedValue.Kind.ToString(),
                    transition.OutputCondition?.ExpectedValue.CanonicalValue))
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
            definition.ProductModel.ModelCode,
            definition.Operations.Count,
            definition.UpdatedAtUtc);
    }
}
