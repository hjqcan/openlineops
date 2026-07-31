using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Traceability.Application.Artifacts;

namespace OpenLineOps.Traceability.Api.RuntimeIntegration;

public sealed class TraceStationArtifactReceiptVerifier(ITraceArtifactStorage storage)
    : IStationArtifactReceiptVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async ValueTask VerifyAsync(
        StationJobCompleted completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        StationMessageContract.Validate(completion);
        foreach (var artifact in completion.Artifacts)
        {
            var receiptKey = StationArtifactReceiptIdentity.ReceiptStorageKey(artifact.ReceiptId);
            var receiptContentResult = await storage.OpenReadAsync(receiptKey, cancellationToken)
                .ConfigureAwait(false);
            if (receiptContentResult.IsFailure)
            {
                throw Rejected(
                    $"Station artifact receipt {artifact.ReceiptId} is not durably stored: "
                    + receiptContentResult.Error.Code);
            }

            StationArtifactReceipt receipt;
            await using (var receiptContent = receiptContentResult.Value.Content)
            {
                try
                {
                    receipt = await JsonSerializer.DeserializeAsync<StationArtifactReceipt>(
                            receiptContent,
                            JsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw Rejected(
                            $"Station artifact receipt {artifact.ReceiptId} is empty.");
                }
                catch (JsonException exception)
                {
                    throw new StationArtifactReceiptRejectedException(
                        $"Station artifact receipt {artifact.ReceiptId} is invalid JSON.",
                        exception);
                }
            }

            try
            {
                StationArtifactReceiptIdentity.Validate(receipt);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                throw new StationArtifactReceiptRejectedException(
                    $"Station artifact receipt {artifact.ReceiptId} is not canonical.",
                    exception);
            }

            if (!string.Equals(receipt.ReceiptId, artifact.ReceiptId, StringComparison.Ordinal)
                || !string.Equals(receipt.AgentId, completion.AgentId, StringComparison.Ordinal)
                || !string.Equals(receipt.StationId, completion.StationId, StringComparison.Ordinal)
                || receipt.JobId != completion.JobId
                || !string.Equals(receipt.ArtifactName, artifact.Name, StringComparison.Ordinal)
                || !string.Equals(receipt.ArtifactKind, artifact.Kind, StringComparison.Ordinal)
                || !string.Equals(receipt.MediaType, artifact.MediaType, StringComparison.Ordinal)
                || receipt.SizeBytes != artifact.SizeBytes
                || !string.Equals(receipt.Sha256, artifact.Sha256, StringComparison.Ordinal)
                || !string.Equals(receipt.StorageKey, artifact.StorageKey, StringComparison.Ordinal))
            {
                throw Rejected(
                    $"Station artifact receipt {artifact.ReceiptId} does not match its completion evidence.");
            }

            var artifactContentResult = await storage
                .OpenReadAsync(artifact.StorageKey, cancellationToken)
                .ConfigureAwait(false);
            if (artifactContentResult.IsFailure)
            {
                throw Rejected(
                    $"Station artifact {artifact.StorageKey} is not durably stored: "
                    + artifactContentResult.Error.Code);
            }

            await using var artifactContent = artifactContentResult.Value.Content;
            if (artifactContentResult.Value.SizeBytes != artifact.SizeBytes
                || !string.Equals(
                    artifactContentResult.Value.Sha256,
                    artifact.Sha256,
                    StringComparison.Ordinal))
            {
                throw Rejected(
                    $"Station artifact {artifact.StorageKey} does not match its durable receipt.");
            }
        }
    }

    private static StationArtifactReceiptRejectedException Rejected(string message) => new(message);
}
