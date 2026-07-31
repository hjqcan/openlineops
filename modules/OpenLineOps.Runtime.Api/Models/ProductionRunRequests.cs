using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductionRunCommandApiRequest(
    string? Reason,
    string? OperationId,
    ProductionRecoveryDecisionApiRequest? RecoveryDecision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductionRecoveryDecisionApiRequest(
    string DecisionId,
    string EvidenceReference,
    string DecidedAtUtc,
    string? OperationRunId,
    string? OperationId,
    string? ObservedJudgement,
    IReadOnlyDictionary<string, ProductionContextValueApiRequest>? ObservedOutputs);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProductionContextValueApiRequest(
    string Kind,
    string CanonicalValue);
