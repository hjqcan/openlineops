namespace OpenLineOps.Runtime.Domain.Stations;

[Flags]
public enum StationCommandGrant
{
    None = 0,
    ChangeMode = 1 << 0,
    ReportReadiness = 1 << 1,
    Reset = 1 << 2,
    Start = 1 << 3,
    Complete = 1 << 4,
    Hold = 1 << 5,
    Unhold = 1 << 6,
    Suspend = 1 << 7,
    Unsuspend = 1 << 8,
    Stop = 1 << 9,
    Abort = 1 << 10,
    Clear = 1 << 11,
    AcknowledgeTransition = 1 << 12,
    All = ChangeMode
        | ReportReadiness
        | Reset
        | Start
        | Complete
        | Hold
        | Unhold
        | Suspend
        | Unsuspend
        | Stop
        | Abort
        | Clear
        | AcknowledgeTransition
}

public sealed record StationCommandAuthorization
{
    public StationCommandAuthorization(
        string actorId,
        StationCommandGrant grants)
    {
        ActorId = StationLifecycleGuard.Canonical(actorId, nameof(actorId));
        if ((grants & ~StationCommandGrant.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grants),
                grants,
                "Station command grants contain undefined flags.");
        }

        Grants = grants;
    }

    public string ActorId { get; }

    public StationCommandGrant Grants { get; }

    public bool Allows(StationCommandGrant grant)
    {
        if (grant is StationCommandGrant.None
            || (grant & ~StationCommandGrant.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(grant),
                grant,
                "A single defined station command grant is required.");
        }

        return (Grants & grant) == grant;
    }
}
