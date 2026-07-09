using Microsoft.AspNetCore.SignalR;

namespace OpenLineOps.Runtime.Api.Hubs;

public sealed class RuntimeProgressHub : Hub<IRuntimeProgressClient>
{
    public Task JoinStationGroup(string stationId)
    {
        return string.IsNullOrWhiteSpace(stationId)
            ? Task.CompletedTask
            : Groups.AddToGroupAsync(Context.ConnectionId, StationGroup(stationId));
    }

    public Task LeaveStationGroup(string stationId)
    {
        return string.IsNullOrWhiteSpace(stationId)
            ? Task.CompletedTask
            : Groups.RemoveFromGroupAsync(Context.ConnectionId, StationGroup(stationId));
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

    internal static string StationGroup(string stationId)
    {
        return $"runtime:station:{stationId.Trim()}";
    }

    internal static string SessionGroup(Guid sessionId)
    {
        return $"runtime:session:{sessionId:D}";
    }
}
