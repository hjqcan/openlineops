using OpenLineOps.Runtime.Api.Models;

namespace OpenLineOps.Runtime.Api.Hubs;

public interface IRuntimeProgressClient
{
    Task RuntimeEvent(RuntimeTimelineEntryResponse entry);

    Task StationStatusChanged(RuntimeStationStatusResponse status);

    Task AlarmRaised(RuntimeAlarmResponse alarm);

    Task AlarmAcknowledged(RuntimeAlarmResponse alarm);
}
