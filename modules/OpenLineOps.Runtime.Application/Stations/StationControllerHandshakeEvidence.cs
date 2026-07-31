using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Stations;

public static class StationControllerHandshakeEvidence
{
    public static string OperationalStateSha256(
        StationControllerHandshake state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var report = state.LatestReport;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString(
                "evidenceType",
                "OpenLineOps.StationControllerOperationalState");
            writer.WriteString("stationId", state.StationId.Value);
            writer.WriteNumber("recoveryEpoch", state.RecoveryEpoch);
            writer.WriteNumber("operationalEpoch", state.OperationalEpoch);
            writer.WriteString("ownerAgentId", report.OwnerAgentId);
            writer.WriteString(
                "ownerAgentInstanceId",
                report.OwnerAgentInstanceId);
            writer.WriteNumber(
                "agentFencingToken",
                report.AgentFencingToken);
            writer.WriteString("controllerSessionId", report.ControllerSessionId);
            writer.WriteNumber("commandSequence", report.CommandSequence);
            writer.WriteNumber(
                "acknowledgedCommandSequence",
                report.AcknowledgedCommandSequence);
            WriteNullable(writer, "commandId", report.CommandId);
            writer.WriteNumber(
                "commandFencingToken",
                report.CommandFencingToken);
            writer.WriteString("observedMode", report.ObservedMode.ToString());
            writer.WriteString("observedState", report.ObservedState.ToString());
            writer.WriteNumber("stateSequence", report.StateSequence);
            writer.WriteBoolean("busy", report.Busy);
            writer.WriteBoolean("completed", report.Completed);
            writer.WriteBoolean("error", report.Error);
            WriteNullable(writer, "errorCode", report.ErrorCode);
            writer.WriteBoolean("recipeConfirmed", report.RecipeConfirmed);
            WriteNullable(writer, "confirmedRecipeId", report.ConfirmedRecipeId);
            WriteNullable(
                writer,
                "confirmedRecipeVersion",
                report.ConfirmedRecipeVersion);
            writer.WriteBoolean(
                "safetyPermitGranted",
                report.SafetyPermitGranted);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static void WriteNullable(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }
}
