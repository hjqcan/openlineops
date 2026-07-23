using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Traceability.Application.Artifacts;

public interface IStationArtifactReceiptService
{
    Task<Result<StationArtifactReceipt>> StoreAsync(
        StoreStationArtifactRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StoreStationArtifactRequest(
    string AgentId,
    string StationId,
    Guid JobId,
    string ArtifactName,
    string ArtifactKind,
    string? MediaType,
    long SizeBytes,
    string Sha256,
    Stream Content);

public sealed class StationArtifactReceiptService(
    ITraceArtifactStorage storage,
    long maximumArtifactSizeBytes) : IStationArtifactReceiptService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<Result<StationArtifactReceipt>> StoreAsync(
        StoreStationArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maximumArtifactSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                "Station artifact maximum size must be greater than zero.");
        }

        if (request.SizeBytes < 0 || request.SizeBytes > maximumArtifactSizeBytes)
        {
            return Result.Failure<StationArtifactReceipt>(ApplicationError.Validation(
                "Traceability.StationArtifactSizeRejected",
                $"Station artifact size must be between zero and {maximumArtifactSizeBytes} bytes."));
        }

        StationArtifactReceipt receipt;
        try
        {
            receipt = StationArtifactReceiptIdentity.Create(
                request.AgentId,
                request.StationId,
                request.JobId,
                request.ArtifactName,
                request.ArtifactKind,
                request.MediaType,
                request.SizeBytes,
                request.Sha256);
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<StationArtifactReceipt>(ApplicationError.Validation(
                "Traceability.StationArtifactIdentityInvalid",
                exception.Message));
        }

        var contentResult = await storage.StoreAsync(
                new StoreTraceArtifactRequest(
                    receipt.StorageKey,
                    receipt.ArtifactName,
                    receipt.MediaType,
                    request.Content,
                    receipt.Sha256,
                    receipt.SizeBytes),
                cancellationToken)
            .ConfigureAwait(false);
        if (contentResult.IsFailure)
        {
            return Result.Failure<StationArtifactReceipt>(contentResult.Error);
        }

        var stored = contentResult.Value;
        if (!string.Equals(stored.StorageKey, receipt.StorageKey, StringComparison.Ordinal)
            || stored.SizeBytes != receipt.SizeBytes
            || !string.Equals(stored.Sha256, receipt.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Central artifact storage returned evidence different from the requested receipt.");
        }

        var receiptDocument = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
        var receiptSha256 = Convert.ToHexStringLower(SHA256.HashData(receiptDocument));
        await using var receiptContent = new MemoryStream(receiptDocument, writable: false);
        var receiptResult = await storage.StoreAsync(
                new StoreTraceArtifactRequest(
                    StationArtifactReceiptIdentity.ReceiptStorageKey(receipt.ReceiptId),
                    $"{receipt.ReceiptId}.json",
                    "application/json",
                    receiptContent,
                    receiptSha256,
                    receiptDocument.LongLength),
                cancellationToken)
            .ConfigureAwait(false);
        if (receiptResult.IsFailure)
        {
            return Result.Failure<StationArtifactReceipt>(receiptResult.Error);
        }

        return Result.Success(receipt);
    }
}
