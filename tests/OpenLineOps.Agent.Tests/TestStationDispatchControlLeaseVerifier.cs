using OpenLineOps.Agent.Application.StationJobs;

namespace OpenLineOps.Agent.Tests;

internal sealed class TestStationDispatchControlLeaseVerifier(
    StationDispatchControlLeaseVerificationResult result) :
    IStationDispatchControlLeaseVerifier
{
    public static TestStationDispatchControlLeaseVerifier Accepting() =>
        new(StationDispatchControlLeaseVerificationResult.Allow());

    public StationDispatchControlLeaseExpectation? LastExpectation { get; private set; }

    public StationDispatchControlLeaseVerificationResult Result { get; set; } = result;

    public ValueTask<StationDispatchControlLeaseVerificationResult>
        ValidateCurrentAsync(
            StationDispatchControlLeaseExpectation expectation,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        cancellationToken.ThrowIfCancellationRequested();
        LastExpectation = expectation;
        return ValueTask.FromResult(Result);
    }
}
