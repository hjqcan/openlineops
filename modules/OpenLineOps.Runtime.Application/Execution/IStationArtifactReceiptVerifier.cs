using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Runtime.Application.Execution;

public interface IStationArtifactReceiptVerifier
{
    ValueTask VerifyAsync(
        StationJobCompleted completion,
        CancellationToken cancellationToken = default);
}

public sealed class StationArtifactReceiptRejectedException : Exception
{
    public StationArtifactReceiptRejectedException(string message)
        : base(message)
    {
    }

    public StationArtifactReceiptRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
