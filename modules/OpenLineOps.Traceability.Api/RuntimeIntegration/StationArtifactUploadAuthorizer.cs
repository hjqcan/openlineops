using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Traceability.Application.Artifacts;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class StationArtifactUploadAuthorizer(
    IStationJobCoordinationStore coordinationStore,
    ITraceArtifactStorage artifactStorage) : IStationArtifactUploadAuthorizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async ValueTask<StationArtifactUploadAuthorization> AuthorizeAsync(
        StationArtifactUploadAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dispatch = await coordinationStore
            .GetDispatchRequestAsync(request.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (dispatch is null)
        {
            return Reject(
                StationArtifactUploadAuthorizationStatus.JobNotFound,
                "StationArtifact.JobNotFound");
        }

        if (!string.Equals(dispatch.AgentId, request.AgentId, StringComparison.Ordinal)
            || !string.Equals(dispatch.StationId, request.StationId, StringComparison.Ordinal))
        {
            return Reject(
                StationArtifactUploadAuthorizationStatus.IdentityForbidden,
                "StationArtifact.JobIdentityForbidden");
        }

        StationArtifactReceipt expectedReceipt;
        try
        {
            expectedReceipt = StationArtifactReceiptIdentity.Create(
                request.AgentId,
                request.StationId,
                request.JobId,
                request.ArtifactName,
                request.ArtifactKind,
                request.MediaType,
                request.SizeBytes,
                request.Sha256);
        }
        catch (ArgumentException)
        {
            return Reject(
                StationArtifactUploadAuthorizationStatus.MetadataInvalid,
                "StationArtifact.MetadataInvalid");
        }

        var completion = await coordinationStore
            .GetCompletionAsync(dispatch.IdempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        if (completion is not null)
        {
            return completion.Artifacts.Any(artifact =>
                    string.Equals(artifact.Name, expectedReceipt.ArtifactName, StringComparison.Ordinal)
                    && string.Equals(artifact.Kind, expectedReceipt.ArtifactKind, StringComparison.Ordinal)
                    && string.Equals(artifact.MediaType, expectedReceipt.MediaType, StringComparison.Ordinal)
                    && artifact.SizeBytes == expectedReceipt.SizeBytes
                    && string.Equals(artifact.Sha256, expectedReceipt.Sha256, StringComparison.Ordinal)
                    && string.Equals(
                        artifact.StorageKey,
                        expectedReceipt.StorageKey,
                        StringComparison.Ordinal)
                    && string.Equals(
                        artifact.ReceiptId,
                        expectedReceipt.ReceiptId,
                        StringComparison.Ordinal))
                ? StationArtifactUploadAuthorization.Authorized
                : Reject(
                    StationArtifactUploadAuthorizationStatus.TerminalConflict,
                    "StationArtifact.CompletionTerminalConflict");
        }

        var recoveryRequired = await coordinationStore
            .GetRecoveryRequiredAsync(request.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (recoveryRequired is null)
        {
            return StationArtifactUploadAuthorization.Authorized;
        }

        return await HasExactDurableReceiptAsync(expectedReceipt, cancellationToken)
                .ConfigureAwait(false)
            ? StationArtifactUploadAuthorization.Authorized
            : Reject(
                StationArtifactUploadAuthorizationStatus.TerminalConflict,
                "StationArtifact.RecoveryTerminalConflict");
    }

    private async ValueTask<bool> HasExactDurableReceiptAsync(
        StationArtifactReceipt expectedReceipt,
        CancellationToken cancellationToken)
    {
        var result = await artifactStorage
            .OpenReadAsync(
                StationArtifactReceiptIdentity.ReceiptStorageKey(expectedReceipt.ReceiptId),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return false;
        }

        await using var content = result.Value.Content;
        try
        {
            var receipt = await JsonSerializer.DeserializeAsync<StationArtifactReceipt>(
                    content,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (receipt is null)
            {
                return false;
            }

            StationArtifactReceiptIdentity.Validate(receipt);
            return receipt == expectedReceipt;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static StationArtifactUploadAuthorization Reject(
        StationArtifactUploadAuthorizationStatus status,
        string failureCode) => StationArtifactUploadAuthorization.Reject(status, failureCode);
}
