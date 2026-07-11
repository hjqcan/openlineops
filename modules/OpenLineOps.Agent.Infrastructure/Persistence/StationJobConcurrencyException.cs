using OpenLineOps.Agent.Domain.StationJobs;

namespace OpenLineOps.Agent.Infrastructure.Persistence;

public sealed class StationJobConcurrencyException : InvalidOperationException
{
    public StationJobConcurrencyException(StationJobId jobId, long expectedRevision)
        : base($"Station job {jobId} no longer has expected revision {expectedRevision}.")
    {
        JobId = jobId;
        ExpectedRevision = expectedRevision;
    }

    public StationJobId JobId { get; }

    public long ExpectedRevision { get; }
}
