using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;

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
            new DutModelDocument(
                definition.DutModel.Id.Value,
                definition.DutModel.ModelCode,
                definition.DutModel.IdentityInputKey),
            definition.Workstations
                .OrderBy(workstation => workstation.Id.Value, StringComparer.Ordinal)
                .Select(workstation => new WorkstationDocument(
                    workstation.Id.Value,
                    workstation.DisplayName,
                    workstation.StationSystemId))
                .ToArray(),
            definition.Stages
                .OrderBy(stage => stage.Sequence)
                .Select(stage => new ProcessStageDocument(
                    stage.Id.Value,
                    stage.Sequence,
                    stage.DisplayName,
                    stage.WorkstationId.Value,
                    stage.FlowDefinitionId,
                    stage.ExternalTestProgramAdapterId?.Value))
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
            DutModelDefinition.Create(
                new DutModelId(document.DutModel.DutModelId),
                document.DutModel.ModelCode,
                document.DutModel.IdentityInputKey),
            document.Workstations.Select(workstation => WorkstationDefinition.Create(
                new WorkstationId(workstation.WorkstationId),
                workstation.DisplayName,
                workstation.StationSystemId)),
            document.Stages.Select(stage => ProcessStage.Create(
                new ProcessStageId(stage.StageId),
                stage.Sequence,
                stage.DisplayName,
                new WorkstationId(stage.WorkstationId),
                stage.FlowDefinitionId,
                string.IsNullOrWhiteSpace(stage.ExternalTestProgramAdapterId)
                    ? null
                    : new ExternalTestProgramAdapterId(stage.ExternalTestProgramAdapterId))),
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

        if (document.DutModel is null
            || document.Workstations is null
            || document.Stages is null
            || document.ExternalTestProgramAdapters is null
            || document.Workstations.Any(static workstation => workstation is null)
            || document.Stages.Any(static stage => stage is null)
            || document.ExternalTestProgramAdapters.Any(adapter =>
                adapter is null
                || adapter.ArgumentTemplates is null
                || adapter.InputMappings is null
                || adapter.ResultMappings is null
                || adapter.ArgumentTemplates.Any(static argument => argument is null)
                || adapter.InputMappings.Any(static mapping => mapping is null)
                || adapter.ResultMappings.Any(static mapping => mapping is null)
                || !IsValidTimeout(adapter.TimeoutMilliseconds)))
        {
            throw new InvalidDataException(
                "Production line resource must contain all required semantic collections.");
        }
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
