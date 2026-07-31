namespace OpenLineOps.Operations.Domain.Repositories;

public enum AlarmCommitOutcome
{
    Committed = 0,
    NoChanges = 1,
    ConcurrencyConflict = 2,
    UniqueConstraintConflict = 3
}
