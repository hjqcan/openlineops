using Microsoft.AspNetCore.SignalR;

namespace OpenLineOps.Runtime.Api.Hubs;

public sealed class RuntimeProgressHub : Hub<IRuntimeProgressClient>
{
    public Task JoinStationSystemGroup(
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionRunId,
        string stationSystemId)
    {
        return Groups.AddToGroupAsync(
            Context.ConnectionId,
            StationSystemGroup(
                projectId,
                applicationId,
                projectSnapshotId,
                topologyId,
                ParseProductionRunId(productionRunId),
                stationSystemId));
    }

    public Task LeaveStationSystemGroup(
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionRunId,
        string stationSystemId)
    {
        return Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            StationSystemGroup(
                projectId,
                applicationId,
                projectSnapshotId,
                topologyId,
                ParseProductionRunId(productionRunId),
                stationSystemId));
    }

    public Task JoinProductionRunGroup(
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionRunId)
    {
        return Groups.AddToGroupAsync(
            Context.ConnectionId,
            ProductionRunGroup(
                projectId,
                applicationId,
                projectSnapshotId,
                topologyId,
                ParseProductionRunId(productionRunId)));
    }

    public Task LeaveProductionRunGroup(
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionRunId)
    {
        return Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            ProductionRunGroup(
                projectId,
                applicationId,
                projectSnapshotId,
                topologyId,
                ParseProductionRunId(productionRunId)));
    }

    public Task JoinSessionGroup(Guid sessionId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
    }

    public Task LeaveSessionGroup(Guid sessionId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
    }

    internal static string StationSystemGroup(
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        Guid productionRunId,
        string stationSystemId)
    {
        return string.Join(
            ':',
            "runtime",
            "station-system",
            GroupSegment(projectId, nameof(projectId)),
            GroupSegment(applicationId, nameof(applicationId)),
            GroupSegment(projectSnapshotId, nameof(projectSnapshotId)),
            GroupSegment(topologyId, nameof(topologyId)),
            ProductionRunSegment(productionRunId),
            GroupSegment(stationSystemId, nameof(stationSystemId)));
    }

    internal static string ProductionRunGroup(
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        Guid productionRunId)
    {
        return string.Join(
            ':',
            "runtime",
            "production-run",
            GroupSegment(projectId, nameof(projectId)),
            GroupSegment(applicationId, nameof(applicationId)),
            GroupSegment(projectSnapshotId, nameof(projectSnapshotId)),
            GroupSegment(topologyId, nameof(topologyId)),
            ProductionRunSegment(productionRunId));
    }

    internal static string SessionGroup(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new HubException("sessionId must be a non-empty Runtime Session ID.");
        }

        return $"runtime:session:{sessionId:D}";
    }

    private static string ProductionRunSegment(Guid productionRunId)
    {
        if (productionRunId == Guid.Empty)
        {
            throw new HubException("productionRunId must be a non-empty Production Run ID.");
        }

        return productionRunId.ToString("D");
    }

    private static Guid ParseProductionRunId(string productionRunId)
    {
        if (!Guid.TryParseExact(productionRunId, "D", out var parsedProductionRunId)
            || parsedProductionRunId == Guid.Empty
            || !string.Equals(
                parsedProductionRunId.ToString("D"),
                productionRunId,
                StringComparison.Ordinal))
        {
            throw new HubException(
                "productionRunId must be a non-empty canonical Production Run ID.");
        }

        return parsedProductionRunId;
    }

    private static string GroupSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1]))
        {
            throw new HubException($"{parameterName} must be a non-empty canonical string.");
        }

        return $"{value.Length}:{value}";
    }
}
