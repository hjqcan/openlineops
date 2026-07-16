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
                    operation.ConfigurationSnapshotId,
                    operation.Resources.Select(resource => new OperationResourceBindingDetails(
                        resource.Id.Value,
                        resource.Kind.ToString(),
                        resource.TopologyTargetId,
                        resource.Resolution.ToString())).ToArray(),
                    operation.InputMappings.Select(mapping => new OperationInputMappingDetails(
                        mapping.TargetInputKey,
                        mapping.SourceOperationId.Value,
                        mapping.SourceOutputKey,
                        mapping.ExpectedValueKind.ToString())).ToArray()))
                .ToArray(),
            definition.Transitions
                .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
                .Select(transition => new RouteTransitionDetails(
                    transition.Id.Value,
                    transition.SourceOperationId.Value,
                    transition.TargetOperationId?.Value,
                    transition.TerminalDisposition?.ToString(),
                    transition.Kind.ToString(),
                    transition.RequiredJudgement?.ToString(),
                    transition.MaxTraversals,
                    transition.ParallelGroupId,
                    transition.OutputCondition?.OutputKey,
                    transition.OutputCondition?.ExpectedValue.Kind.ToString(),
                    transition.OutputCondition?.ExpectedValue.CanonicalValue))
                .ToArray(),
            definition.LineControllerAuthorizations
                .OrderBy(authorization => authorization.Id.Value, StringComparer.Ordinal)
                .Select(authorization => new LineControllerAuthorizationDetails(
                    authorization.Id.Value,
                    authorization.OperationId.Value,
                    authorization.ActionId,
                    authorization.ControllerSystemId,
                    authorization.ControllerBindingId,
                    authorization.ControllerCapabilityId,
                    authorization.ControllerAction,
                    authorization.TargetStationSystemId,
                    authorization.TargetSystemId,
                    authorization.TargetBindingId,
                    authorization.TargetCapabilityId,
                    authorization.TargetAction))
                .ToArray(),
            new ProductionRouteLayoutDetails(definition.RouteLayout.OperationPositions
                .OrderBy(position => position.OperationId.Value, StringComparer.Ordinal)
                .Select(position => new OperationCanvasPositionDetails(
                    position.OperationId.Value,
                    position.X,
                    position.Y))
                .ToArray()),
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
