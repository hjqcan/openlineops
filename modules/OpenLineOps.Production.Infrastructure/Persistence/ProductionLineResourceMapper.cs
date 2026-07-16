using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Production.Infrastructure.Persistence;

internal static class ProductionLineResourceMapper
{
    public static ProductionLineResourceDocument FromAggregate(
        ProjectApplicationWorkspaceScope scope,
        ProductionLineDefinition definition)
    {
        return new ProductionLineResourceDocument(
            ProductionLineResourceDocument.CurrentSchemaVersion,
            ProductionLineResourceDocument.Kind,
            scope.ApplicationId,
            definition.Id.Value,
            definition.DisplayName,
            definition.TopologyId,
            new ProductModelDocument(
                definition.ProductModel.Id.Value,
                definition.ProductModel.ModelCode,
                definition.ProductModel.IdentityInputKey),
            definition.EntryOperationId.Value,
            definition.Operations
                .OrderBy(operation => operation.Id.Value, StringComparer.Ordinal)
                .Select(operation => new OperationDefinitionDocument(
                    operation.Id.Value,
                    operation.DisplayName,
                    operation.StationSystemId,
                    operation.FlowDefinitionId,
                    operation.ConfigurationSnapshotId,
                    operation.Resources.Select(resource => new OperationResourceBindingDocument(
                        resource.Id.Value,
                        resource.Kind.ToString(),
                        resource.TopologyTargetId,
                        resource.Resolution.ToString())).ToArray(),
                    operation.InputMappings.Select(mapping => new OperationInputMappingDocument(
                        mapping.TargetInputKey,
                        mapping.SourceOperationId.Value,
                        mapping.SourceOutputKey,
                        mapping.ExpectedValueKind.ToString())).ToArray()))
                .ToArray(),
            definition.Transitions
                .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
                .Select(transition => new RouteTransitionDocument(
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
                .Select(authorization => new LineControllerAuthorizationDocument(
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
            new ProductionRouteLayoutDocument(definition.RouteLayout.OperationPositions
                .OrderBy(position => position.OperationId.Value, StringComparer.Ordinal)
                .Select(position => new OperationCanvasPositionDocument(
                    position.OperationId.Value,
                    position.X,
                    position.Y))
                .ToArray()),
            definition.CreatedAtUtc,
            definition.UpdatedAtUtc);
    }

    public static ProductionLineDefinition ToAggregate(
        ProjectApplicationWorkspaceScope scope,
        ProductionLineResourceDocument document)
    {
        Validate(scope, document);
        return ProductionLineDefinition.Restore(
            new ProductionLineDefinitionId(document.LineDefinitionId),
            document.DisplayName,
            document.TopologyId,
            ProductModelDefinition.Create(
                new ProductModelId(document.ProductModel.ProductModelId),
                document.ProductModel.ModelCode,
                document.ProductModel.IdentityInputKey),
            new OperationDefinitionId(document.EntryOperationId),
            document.Operations.Select(operation => OperationDefinition.Create(
                new OperationDefinitionId(operation.OperationId),
                operation.DisplayName,
                operation.StationSystemId,
                operation.FlowDefinitionId,
                operation.ConfigurationSnapshotId,
                operation.Resources.Select(resource => new OperationResourceBinding(
                    new OperationResourceBindingId(resource.BindingId),
                    ParseExact<OperationResourceKind>(resource.Kind, "Operation resource kind"),
                    resource.TopologyTargetId,
                    ParseExact<OperationResourceResolution>(
                        resource.Resolution,
                        "Operation resource resolution"))),
                operation.InputMappings.Select(mapping => new OperationInputMapping(
                    mapping.TargetInputKey,
                    new OperationDefinitionId(mapping.SourceOperationId),
                    mapping.SourceOutputKey,
                    ParseExact<ProductionContextValueKind>(
                        mapping.ExpectedValueKind,
                        "Operation input mapping value kind"))))),
            document.Transitions.Select(transition => RouteTransition.Create(
                new RouteTransitionId(transition.TransitionId),
                new OperationDefinitionId(transition.SourceOperationId),
                transition.TargetOperationId is null
                    ? null
                    : new OperationDefinitionId(transition.TargetOperationId),
                transition.TerminalDisposition is null
                    ? null
                    : ParseExact<TerminalDisposition>(
                        transition.TerminalDisposition,
                        "terminal disposition"),
                ParseExact<RouteTransitionKind>(transition.Kind, "route transition kind"),
                transition.RequiredJudgement is null
                    ? null
                    : ParseExact<RouteJudgement>(transition.RequiredJudgement, "route judgement"),
                transition.MaxTraversals,
                transition.ParallelGroupId,
                transition.OutputKey is null
                    ? null
                    : new RouteOutputCondition(
                        transition.OutputKey,
                        new ProductionContextValue(
                            ParseExact<ProductionContextValueKind>(
                                transition.ExpectedOutputKind!,
                                "Production Context value kind"),
                            transition.ExpectedOutputValue!)))),
            document.LineControllerAuthorizations.Select(authorization =>
                new LineControllerAuthorization(
                    new LineControllerAuthorizationId(authorization.AuthorizationId),
                    new OperationDefinitionId(authorization.OperationId),
                    authorization.ActionId,
                    authorization.ControllerSystemId,
                    authorization.ControllerBindingId,
                    authorization.ControllerCapabilityId,
                    authorization.ControllerAction,
                    authorization.TargetStationSystemId,
                    authorization.TargetSystemId,
                    authorization.TargetBindingId,
                    authorization.TargetCapabilityId,
                    authorization.TargetAction)),
            new ProductionRouteLayout(document.RouteLayout.OperationPositions.Select(position =>
                new OperationCanvasPosition(
                    new OperationDefinitionId(position.OperationId),
                    position.X,
                    position.Y))),
            document.CreatedAtUtc,
            document.UpdatedAtUtc);
    }

    private static void Validate(
        ProjectApplicationWorkspaceScope scope,
        ProductionLineResourceDocument document)
    {
        if (!string.Equals(
                document.SchemaVersion,
                ProductionLineResourceDocument.CurrentSchemaVersion,
                StringComparison.Ordinal)
            || !string.Equals(document.ResourceKind, ProductionLineResourceDocument.Kind, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Production line resource has an unsupported schema or resource kind.");
        }

        if (!string.Equals(document.ApplicationId, scope.ApplicationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Production line belongs to Application {document.ApplicationId}, not {scope.ApplicationId}.");
        }

        if (document.ProductModel is null
            || string.IsNullOrWhiteSpace(document.EntryOperationId)
            || document.Operations is null
            || document.Transitions is null
            || document.LineControllerAuthorizations is null
            || document.RouteLayout is null
            || document.RouteLayout.OperationPositions is null
            || document.Operations.Any(static operation => operation is null
                || string.IsNullOrWhiteSpace(operation.ConfigurationSnapshotId)
                || !string.Equals(
                    operation.ConfigurationSnapshotId,
                    operation.ConfigurationSnapshotId.Trim(),
                    StringComparison.Ordinal)
                || operation.Resources is null
                || operation.Resources.Length == 0
                || operation.Resources.Any(static resource => resource is null
                    || string.IsNullOrWhiteSpace(resource.BindingId)
                    || string.IsNullOrWhiteSpace(resource.Kind)
                    || string.IsNullOrWhiteSpace(resource.TopologyTargetId)
                    || string.IsNullOrWhiteSpace(resource.Resolution))
                || operation.InputMappings is null
                || operation.InputMappings.Any(static mapping => mapping is null
                    || string.IsNullOrWhiteSpace(mapping.TargetInputKey)
                    || string.IsNullOrWhiteSpace(mapping.SourceOperationId)
                    || string.IsNullOrWhiteSpace(mapping.SourceOutputKey)
                    || string.IsNullOrWhiteSpace(mapping.ExpectedValueKind)))
            || document.Transitions.Any(static transition => transition is null
                || string.IsNullOrWhiteSpace(transition.Kind)
                || (string.IsNullOrWhiteSpace(transition.TargetOperationId)
                    == string.IsNullOrWhiteSpace(transition.TerminalDisposition))
                || (string.Equals(
                        transition.Kind,
                        RouteTransitionKind.Condition.ToString(),
                        StringComparison.Ordinal)
                    != (transition.OutputKey is not null
                        && transition.ExpectedOutputKind is not null
                        && transition.ExpectedOutputValue is not null)))
            || document.LineControllerAuthorizations.Any(static authorization => authorization is null
                || string.IsNullOrWhiteSpace(authorization.AuthorizationId)
                || string.IsNullOrWhiteSpace(authorization.OperationId)
                || string.IsNullOrWhiteSpace(authorization.ActionId)
                || string.IsNullOrWhiteSpace(authorization.ControllerSystemId)
                || string.IsNullOrWhiteSpace(authorization.ControllerBindingId)
                || string.IsNullOrWhiteSpace(authorization.ControllerCapabilityId)
                || string.IsNullOrWhiteSpace(authorization.ControllerAction)
                || string.IsNullOrWhiteSpace(authorization.TargetStationSystemId)
                || string.IsNullOrWhiteSpace(authorization.TargetSystemId)
                || string.IsNullOrWhiteSpace(authorization.TargetBindingId)
                || string.IsNullOrWhiteSpace(authorization.TargetCapabilityId)
                || string.IsNullOrWhiteSpace(authorization.TargetAction))
            || document.RouteLayout.OperationPositions.Any(static position => position is null
                || string.IsNullOrWhiteSpace(position.OperationId)))
        {
            throw new InvalidDataException(
                "Production line resource must contain all required semantic collections.");
        }
    }

    private static T ParseExact<T>(string value, string description)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed)
            || !string.Equals(value, parsed.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Production line resource contains unsupported {description} '{value}'.");
        }

        return parsed;
    }

}
