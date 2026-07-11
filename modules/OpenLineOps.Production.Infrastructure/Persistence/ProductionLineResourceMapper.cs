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
                    operation.ConfigurationSnapshotId))
                .ToArray(),
            definition.Transitions
                .OrderBy(transition => transition.Id.Value, StringComparer.Ordinal)
                .Select(transition => new RouteTransitionDocument(
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
                .Select(adapter => new ExternalTestProgramAdapterDocument(
                    adapter.Id.Value,
                    adapter.DisplayName,
                    adapter.CapabilityId,
                    adapter.CommandName,
                    adapter.Executable,
                    adapter.ProviderKey,
                    adapter.ArgumentTemplates.ToArray(),
                    adapter.InputMappings.Select(mapping =>
                        new ExternalTestProgramInputMappingDocument(mapping.Source, mapping.Target)).ToArray(),
                    adapter.ResultMappings.Select(mapping =>
                        new ExternalTestProgramResultMappingDocument(mapping.SourcePath, mapping.TargetKey)).ToArray(),
                    new ExternalTestProgramOutcomeMappingDocument(
                        adapter.OutcomeMapping.SourcePath,
                        adapter.OutcomeMapping.PassedToken,
                        adapter.OutcomeMapping.FailedToken,
                        adapter.OutcomeMapping.AbortedToken),
                    checked(adapter.Timeout.Ticks / TimeSpan.TicksPerMillisecond)))
                .ToArray(),
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
                operation.ConfigurationSnapshotId)),
            document.Transitions.Select(transition => RouteTransition.Create(
                new RouteTransitionId(transition.TransitionId),
                new OperationDefinitionId(transition.SourceOperationId),
                new OperationDefinitionId(transition.TargetOperationId),
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
            document.ExternalTestProgramAdapters.Select(adapter => ExternalTestProgramAdapter.Create(
                new ExternalTestProgramAdapterId(adapter.AdapterId),
                adapter.DisplayName,
                adapter.CapabilityId,
                adapter.CommandName,
                adapter.Executable,
                adapter.ProviderKey,
                adapter.ArgumentTemplates,
                adapter.InputMappings.Select(mapping =>
                    new ExternalTestProgramInputMapping(mapping.Source, mapping.Target)),
                adapter.ResultMappings.Select(mapping =>
                    new ExternalTestProgramResultMapping(mapping.SourcePath, mapping.TargetKey)),
                new ExternalTestProgramOutcomeMapping(
                    adapter.OutcomeMapping.SourcePath,
                    adapter.OutcomeMapping.PassedToken,
                    adapter.OutcomeMapping.FailedToken,
                    adapter.OutcomeMapping.AbortedToken),
                MillisecondsToTimeout(adapter.TimeoutMilliseconds))),
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
            || document.ExternalTestProgramAdapters is null
            || document.Operations.Any(static operation => operation is null
                || string.IsNullOrWhiteSpace(operation.ConfigurationSnapshotId)
                || !string.Equals(
                    operation.ConfigurationSnapshotId,
                    operation.ConfigurationSnapshotId.Trim(),
                    StringComparison.Ordinal))
            || document.Transitions.Any(static transition => transition is null
                || string.IsNullOrWhiteSpace(transition.Kind)
                || (string.Equals(
                        transition.Kind,
                        RouteTransitionKind.Condition.ToString(),
                        StringComparison.Ordinal)
                    != (transition.OutputKey is not null
                        && transition.ExpectedOutputKind is not null
                        && transition.ExpectedOutputValue is not null)))
            || document.ExternalTestProgramAdapters.Any(adapter =>
                adapter is null
                || adapter.ArgumentTemplates is null
                || adapter.InputMappings is null
                || adapter.ResultMappings is null
                || adapter.OutcomeMapping is null
                || adapter.ArgumentTemplates.Any(static argument => argument is null)
                || adapter.InputMappings.Any(static mapping => mapping is null)
                || adapter.ResultMappings.Any(static mapping => mapping is null)
                || !IsValidTimeout(adapter.TimeoutMilliseconds)))
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

    private static bool IsValidTimeout(long timeoutMilliseconds)
    {
        var maximumMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        return timeoutMilliseconds > 0 && timeoutMilliseconds <= maximumMilliseconds;
    }

    private static TimeSpan MillisecondsToTimeout(long timeoutMilliseconds)
    {
        if (!IsValidTimeout(timeoutMilliseconds))
        {
            throw new InvalidDataException(
                "External test program timeout must be a positive whole number of milliseconds representable by TimeSpan.");
        }

        return TimeSpan.FromTicks(checked(timeoutMilliseconds * TimeSpan.TicksPerMillisecond));
    }
}
