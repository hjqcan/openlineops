using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Runtime.Application.Execution;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Api.RuntimeIntegration;
using OpenLineOps.Traceability.Application.Artifacts;
using OpenLineOps.Traceability.Infrastructure.Artifacts;

namespace OpenLineOps.Api.Tests;

public sealed class StationArtifactReceiptVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OpenLineOps",
        $"receipt-verifier-{Guid.NewGuid():N}");

    [Fact]
    public async Task DurableReceiptReplayVerifiesBeforeCompletionCanBeRecorded()
    {
        var content = "vendor evidence"u8.ToArray();
        var storage = new FileSystemTraceArtifactStorage(_root, 1024);
        var service = new StationArtifactReceiptService(storage, 1024);
        var request = Request(content);

        var stored = await service.StoreAsync(request);
        var replay = await service.StoreAsync(Request(content, request.JobId));

        Assert.True(stored.IsSuccess, stored.Error.Message);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(stored.Value, replay.Value);
        var completion = Completion(stored.Value);
        await new TraceStationArtifactReceiptVerifier(storage).VerifyAsync(completion);
    }

    [Fact]
    public async Task MissingReceiptRejectsCompletion()
    {
        var content = "missing receipt"u8.ToArray();
        var request = Request(content);
        var receipt = StationArtifactReceiptIdentity.Create(
            request.AgentId,
            request.StationId,
            request.JobId,
            request.ArtifactName,
            request.ArtifactKind,
            request.MediaType,
            request.SizeBytes,
            request.Sha256);
        var storage = new FileSystemTraceArtifactStorage(_root, 1024);

        await Assert.ThrowsAsync<StationArtifactReceiptRejectedException>(() =>
            new TraceStationArtifactReceiptVerifier(storage)
                .VerifyAsync(Completion(receipt))
                .AsTask());
    }

    [Fact]
    public async Task TamperedCentralContentRejectsCompletion()
    {
        var content = "original artifact"u8.ToArray();
        var storage = new FileSystemTraceArtifactStorage(_root, 1024);
        var service = new StationArtifactReceiptService(storage, 1024);
        var stored = await service.StoreAsync(Request(content));
        Assert.True(stored.IsSuccess, stored.Error.Message);
        var artifactPath = Path.Combine(
            _root,
            stored.Value.StorageKey.Replace('/', Path.DirectorySeparatorChar));
        File.SetAttributes(
            artifactPath,
            File.GetAttributes(artifactPath) & ~FileAttributes.ReadOnly);
        await File.WriteAllBytesAsync(artifactPath, "tampered artifact"u8.ToArray());

        await Assert.ThrowsAsync<StationArtifactReceiptRejectedException>(() =>
            new TraceStationArtifactReceiptVerifier(storage)
                .VerifyAsync(Completion(stored.Value))
                .AsTask());
    }

    private static StoreStationArtifactRequest Request(byte[] content, Guid? jobId = null) => new(
        "agent.verifier",
        "station.verifier",
        jobId ?? Guid.NewGuid(),
        "vendor-result.json",
        "VendorReport",
        "application/json",
        content.LongLength,
        Convert.ToHexStringLower(SHA256.HashData(content)),
        new MemoryStream(content, writable: false));

    private static StationJobCompleted Completion(StationArtifactReceipt receipt) => new(
        Guid.NewGuid(),
        receipt.JobId,
        $"receipt-verifier/{receipt.JobId:N}",
        receipt.AgentId,
        receipt.StationId,
        Guid.NewGuid(),
        ExecutionStatus.Completed,
        ResultJudgement.Passed,
        JsonSerializer.SerializeToElement(new
        {
            outcome = new
            {
                kind = "Text",
                value = "Passed"
            }
        }),
        0,
        0,
        0,
        [],
        [],
        [],
        [new StationJobArtifact(
            receipt.ArtifactName,
            "VendorReport",
            receipt.StorageKey,
            receipt.ReceiptId,
            receipt.MediaType,
            receipt.SizeBytes,
            receipt.Sha256)],
        null,
        null,
        DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     _root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(_root, recursive: true);
    }
}
