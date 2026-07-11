namespace OpenLineOps.Production.Application.LineDefinitions;

public sealed record ProductionLineDefinitionDetails(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    ProductModelDetails ProductModel,
    string EntryOperationId,
    IReadOnlyCollection<OperationDefinitionDetails> Operations,
    IReadOnlyCollection<RouteTransitionDetails> Transitions,
    IReadOnlyCollection<ExternalTestProgramAdapterDetails> ExternalTestProgramAdapters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProductionLineDefinitionSummary(
    string LineDefinitionId,
    string DisplayName,
    string TopologyId,
    string ProductModelCode,
    int OperationCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record ProductModelDetails(
    string ProductModelId,
    string ModelCode,
    string IdentityInputKey);

public sealed record OperationDefinitionDetails(
    string OperationId,
    string DisplayName,
    string StationSystemId,
    string FlowDefinitionId,
    string ConfigurationSnapshotId);

public sealed record RouteTransitionDetails(
    string TransitionId,
    string SourceOperationId,
    string TargetOperationId,
    string Kind,
    string? RequiredJudgement,
    int? MaxTraversals,
    string? ParallelGroupId,
    string? OutputKey,
    string? ExpectedOutputKind,
    string? ExpectedOutputValue);

public sealed record ExternalTestProgramAdapterDetails(
    string AdapterId,
    string DisplayName,
    string CapabilityId,
    string CommandName,
    string LaunchKind,
    string? Executable,
    string? ProviderKey,
    IReadOnlyCollection<string> ArgumentTemplates,
    IReadOnlyCollection<ExternalTestProgramInputMappingDetails> InputMappings,
    IReadOnlyCollection<ExternalTestProgramResultMappingDetails> ResultMappings,
    ExternalTestProgramOutcomeMappingDetails OutcomeMapping,
    long TimeoutMilliseconds);

public sealed record ExternalTestProgramInputMappingDetails(string Source, string Target);

public sealed record ExternalTestProgramResultMappingDetails(string SourcePath, string TargetKey);

public sealed record ExternalTestProgramOutcomeMappingDetails(
    string SourcePath,
    string PassedToken,
    string FailedToken,
    string AbortedToken);
