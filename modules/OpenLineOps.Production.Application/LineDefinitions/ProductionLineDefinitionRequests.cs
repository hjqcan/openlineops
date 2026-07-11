using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed record SaveProductionLineDefinitionRequest(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    ProductModelRequest ProductModel,
    string EntryOperationId,
    IReadOnlyCollection<OperationDefinitionRequest> Operations,
    IReadOnlyCollection<RouteTransitionRequest> Transitions,
    IReadOnlyCollection<ExternalTestProgramAdapterRequest> ExternalTestProgramAdapters);

public sealed record ProductModelRequest(
    string ProductModelId,
    string ModelCode,
    string IdentityInputKey);

public sealed record OperationDefinitionRequest(
    string OperationId,
    string DisplayName,
    string StationSystemId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId);

public sealed record RouteTransitionRequest(
    string TransitionId,
    string SourceOperationId,
    string TargetOperationId,
    RouteTransitionKind Kind,
    RouteJudgement? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    ProductionContextValueKind? ExpectedOutputKind,
    string? ExpectedOutputValue);

public sealed record ExternalTestProgramAdapterRequest(
    string AdapterId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    string? Executable,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalTestProgramInputMappingRequest> InputMappings,
    IReadOnlyCollection<ExternalTestProgramResultMappingRequest> ResultMappings,
    ExternalTestProgramOutcomeMappingRequest OutcomeMapping,
    long TimeoutMilliseconds);

public sealed record ExternalTestProgramInputMappingRequest(string Source, string Target);

public sealed record ExternalTestProgramResultMappingRequest(string SourcePath, string TargetKey);

public sealed record ExternalTestProgramOutcomeMappingRequest(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);
