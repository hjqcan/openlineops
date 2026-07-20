using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.ContentProtection;
using OpenLineOps.StationRuntime.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Execution;

public sealed class StationResourceFenceAuthorityServer
{
    private readonly StationJobSnapshot _job;
    private readonly IStationResourceFenceValidator _validator;
    private readonly string _accessToken;
    private readonly string _authorizedPrincipalSid;
    private readonly TimeSpan _requestFrameTimeout;

    public StationResourceFenceAuthorityServer(
        StationJobSnapshot job,
        IStationResourceFenceValidator validator,
        string authorizedPrincipalSid,
        TimeSpan requestFrameTimeout = default)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _authorizedPrincipalSid = string.IsNullOrWhiteSpace(authorizedPrincipalSid)
            ? throw new ArgumentException(
                "Resource fence authority principal SID is required.",
                nameof(authorizedPrincipalSid))
            : authorizedPrincipalSid;
        _requestFrameTimeout = requestFrameTimeout == default
            ? TimeSpan.FromSeconds(5)
            : requestFrameTimeout > TimeSpan.Zero
              && requestFrameTimeout <= TimeSpan.FromMinutes(1)
                ? requestFrameTimeout
                : throw new ArgumentOutOfRangeException(
                    nameof(requestFrameTimeout),
                    "Resource fence request timeout must be within 1 tick and 1 minute.");
        _accessToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Descriptor = new StationResourceFenceAuthorityDescriptor(
            $"openlineops-fence-{job.JobId.Value:N}-{Guid.NewGuid():N}",
            _accessToken,
            _authorizedPrincipalSid);
    }

    public StationResourceFenceAuthorityDescriptor Descriptor { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Resource fence authority requires a Windows identity-bound named pipe.");
        }

        await using var pipe = WindowsIdentityBoundNamedPipe.CreateServer(
            Descriptor.PipeName,
            _authorizedPrincipalSid,
            maximumServerInstances: 1,
            inputBufferSize: 1024 * 1024 + sizeof(int),
            outputBufferSize: 1024 * 1024 + sizeof(int));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var requestDeadline =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestDeadline.CancelAfter(_requestFrameTimeout);
                await HandleAsync(pipe, requestDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A connected caller did not finish one bounded fence request.
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
            finally
            {
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
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
        await StationResourceFenceAuthorityWire.ReadResponseReceiptAsync(pipe, cancellationToken)
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
