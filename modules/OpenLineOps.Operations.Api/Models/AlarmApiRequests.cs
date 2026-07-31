using System.Text.Json.Serialization;
using OpenLineOps.Operations.Domain.Shared.Enums;

namespace OpenLineOps.Operations.Api.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterAlarmDefinitionApiRequest(
    string Id,
    string StationId,
    string Source,
    AlarmSeverity Severity,
    string Title,
    string Description,
    bool IsLatching,
    bool RequiresBuzzer,
    int MaximumShelfSeconds,
    int? EscalationDelaySeconds,
    AlarmPolicyAction EscalationAction,
    string CommandId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RaiseAlarmApiRequest(
    string Id,
    string StationId,
    string Source,
    string? SourceId,
    AlarmSeverity Severity,
    string Title,
    string Description,
    DateTimeOffset? RaisedAtUtc,
    string? DefinitionId,
    string? CommandId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcknowledgeAlarmApiRequest(
    string? Comment = null,
    string? CommandId = null,
    long? ExpectedVersion = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClearAlarmSourceApiRequest(
    string ClearanceNote,
    string? CommandId = null,
    long? ExpectedVersion = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ShelfAlarmApiRequest(
    string Comment,
    DateTimeOffset ShelvedUntilUtc,
    string CommandId,
    long ExpectedVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SuppressAlarmApiRequest(
    string SuppressionSource,
    string Reason,
    DateTimeOffset SuppressedUntilUtc,
    string CommandId,
    long ExpectedVersion);
