using System.IO.Pipes;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.StationRuntime.Contracts;

namespace OpenLineOps.StationRuntime;

internal sealed class AgentResourceLeaseFenceRepository : IResourceLeaseRepository
{
    private readonly StationOperationRequestDocument _request;
    private readonly Dictionary<ResourceRequirement, StationOperationResourceFence> _expected;

    public AgentResourceLeaseFenceRepository(StationOperationRequestDocument request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        StationOperationDocumentJson.Validate(request);
        _expected = request.ResourceFences.ToDictionary(
            static fence => new ResourceRequirement(
                ParseKind(fence.ResourceKind),
                fence.ResourceId));
    }

    public ValueTask<IReadOnlyCollection<ResourceLease>> ListAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyCollection<ResourceLease>>(ImmutableOperation());

    public ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        DateTimeOffset acquiredAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyCollection<ResourceLease>?>(ImmutableOperation());

    public async ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (runId.Value != _request.ProductionRunId
            || !string.Equals(operationRunId, _request.OperationRunId, StringComparison.Ordinal)
            || validatedAtUtc.Offset != TimeSpan.Zero
            || evidence.Count != _expected.Count
            || evidence.Any(item => !_expected.TryGetValue(item.Resource, out var expected)
                || expected.FencingToken != item.FencingToken
                || expected.ExpiresAtUtc != item.ExpiresAtUtc))
        {
            return ResourceLeaseFenceValidationResult.Reject(
                "Runtime command resource fence evidence differs from the Station Job.");
        }

        await using var pipe = new NamedPipeClientStream(
            ".",
            _request.ResourceFenceAuthority.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var authorityRequest = new StationResourceFenceValidationRequest(
                StationOperationDocumentContract.ResourceFenceValidationRequestSchema,
                _request.ResourceFenceAuthority.AccessToken,
                _request.JobId,
                _request.ProductionRunId,
                _request.OperationRunId,
                evidence.Select(static item => new StationOperationResourceFence(
                        item.Resource.Kind.ToString(),
                        item.Resource.ResourceId,
                        item.FencingToken,
                        item.ExpiresAtUtc))
                    .OrderBy(static item => item.ResourceKind, StringComparer.Ordinal)
                    .ThenBy(static item => item.ResourceId, StringComparer.Ordinal)
                    .ToArray());
            StationOperationDocumentJson.Validate(authorityRequest);
            await StationResourceFenceAuthorityWire.WriteAsync(
                    pipe,
                    authorityRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            var response = await StationResourceFenceAuthorityWire
                .ReadAsync<StationResourceFenceValidationResponse>(pipe, cancellationToken)
                .ConfigureAwait(false);
            StationOperationDocumentJson.Validate(response);
            return response.Accepted
                ? ResourceLeaseFenceValidationResult.Accept()
                : ResourceLeaseFenceValidationResult.Reject(response.RejectionReason!);
        }
        catch (Exception exception) when (exception is IOException
                                           or TimeoutException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            return ResourceLeaseFenceValidationResult.Reject(
                $"Agent resource fence authority is unavailable: {exception.Message}");
        }
    }

    public ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(ImmutableOperation());

    public ValueTask HoldForRecoveryAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(ImmutableOperation());

    private static InvalidOperationException ImmutableOperation() =>
        new("Station Runtime may validate Agent resource leases but cannot mutate them.");

    private static ResourceKind ParseKind(string value) =>
        Enum.TryParse<ResourceKind>(value, ignoreCase: false, out var kind)
        && Enum.IsDefined(kind)
        && string.Equals(kind.ToString(), value, StringComparison.Ordinal)
            ? kind
            : throw new InvalidDataException(
                $"Station Job resource kind '{value}' is not canonical.");
}
