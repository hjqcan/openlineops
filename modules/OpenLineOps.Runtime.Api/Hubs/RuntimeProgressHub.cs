using Microsoft.AspNetCore.SignalR;

namespace OpenLineOps.Runtime.Api.Hubs;

public sealed class RuntimeProgressHub : Hub<IRuntimeProgressClient>
{
    public Task JoinStationSystemGroup(string stationSystemId)
    {
        return string.IsNullOrWhiteSpace(stationSystemId)
            ? Task.CompletedTask
            : Groups.AddToGroupAsync(Context.ConnectionId, StationSystemGroup(stationSystemId));
    }

    public Task LeaveStationSystemGroup(string stationSystemId)
    {
        return string.IsNullOrWhiteSpace(stationSystemId)
            ? Task.CompletedTask
            : Groups.RemoveFromGroupAsync(Context.ConnectionId, StationSystemGroup(stationSystemId));
    }

    public Task JoinSessionGroup(Guid sessionId)
    {
        return sessionId == Guid.Empty
            ? Task.CompletedTask
            : Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
    }

    public Task LeaveSessionGroup(Guid sessionId)
    {
        return sessionId == Guid.Empty
            ? Task.CompletedTask
            : Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
    }

    internal static string StationSystemGroup(string stationSystemId)
    {
        return $"runtime:station-system:{stationSystemId.Trim()}";
    }

    internal static string SessionGroup(Guid sessionId)
    {
        return $"runtime:session:{sessionId:D}";
    }
}
