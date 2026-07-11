using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.StationRuntime.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Execution;

public sealed class StationResourceFenceAuthorityServer
{
    private readonly StationJobSnapshot _job;
    private readonly IStationResourceFenceValidator _validator;
    private readonly string _accessToken;

    public StationResourceFenceAuthorityServer(
        StationJobSnapshot job,
        IStationResourceFenceValidator validator)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _accessToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Descriptor = new StationResourceFenceAuthorityDescriptor(
            $"openlineops-fence-{job.JobId.Value:N}-{Guid.NewGuid():N}",
            _accessToken);
    }

    public StationResourceFenceAuthorityDescriptor Descriptor { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                Descriptor.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await HandleAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException
                                               or InvalidDataException
                                               or UnauthorizedAccessException)
            {
                if (pipe.IsConnected)
                {
                    await TryWriteRejectionAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async ValueTask HandleAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var request = await StationResourceFenceAuthorityWire
            .ReadAsync<StationResourceFenceValidationRequest>(pipe, cancellationToken)
            .ConfigureAwait(false);
        StationOperationDocumentJson.Validate(request);

        StationResourceFenceValidationResponse response;
        if (!AccessTokenMatches(request.AccessToken)
            || request.JobId != _job.JobId.Value
            || request.ProductionRunId != _job.ProductionRunId
            || !string.Equals(
                request.OperationRunId,
                _job.OperationRunId.Value,
                StringComparison.Ordinal)
            || !EvidenceMatches(request.ResourceFences, _job.ResourceFences))
        {
            response = Reject("Resource fence validation request does not match the active Station Job.");
        }
        else
        {
            var validation = await _validator.ValidateCurrentAsync(_job, cancellationToken)
                .ConfigureAwait(false);
            response = validation.Accepted
                ? new StationResourceFenceValidationResponse(
                    StationOperationDocumentContract.ResourceFenceValidationResponseSchema,
                    true,
                    null)
                : Reject(validation.RejectionReason
                    ?? "Station resource fencing authority rejected the command.");
        }

        StationOperationDocumentJson.Validate(response);
        await StationResourceFenceAuthorityWire.WriteAsync(pipe, response, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool AccessTokenMatches(string supplied)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(_accessToken);
        var suppliedBytes = Encoding.ASCII.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static bool EvidenceMatches(
        IReadOnlyList<StationOperationResourceFence> supplied,
        IReadOnlyList<StationResourceFenceEvidence> expected)
    {
        if (supplied.Count != expected.Count)
        {
            return false;
        }

        var expectedByResource = expected.ToDictionary(
            static fence => (fence.ResourceKind, fence.ResourceId));
        return supplied.All(fence =>
            expectedByResource.TryGetValue(
                (fence.ResourceKind, fence.ResourceId),
                out var match)
            && match.FencingToken == fence.FencingToken
            && match.ExpiresAtUtc == fence.ExpiresAtUtc);
    }

    private static StationResourceFenceValidationResponse Reject(string reason) =>
        new(
            StationOperationDocumentContract.ResourceFenceValidationResponseSchema,
            false,
            reason);

    private static async ValueTask TryWriteRejectionAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        try
        {
            await StationResourceFenceAuthorityWire.WriteAsync(
                    pipe,
                    Reject("Resource fence authority rejected an invalid request."),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                           or OperationCanceledException
                                           or ObjectDisposedException)
        {
        }
    }
}
