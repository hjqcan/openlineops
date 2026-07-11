using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Persistence;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Agent.Tests;

public sealed class StationArtifactTransferTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openlineops-station-artifacts-{Guid.NewGuid():N}");

    [Fact]
    public async Task OfflineCompletionUploadsIdempotentlyAndCleansOnlyAfterBrokerAcknowledgement()
    {
        var localRoot = Path.Combine(_root, "local");
        var exchangeRoot = Path.Combine(_root, "exchange");
        var jobId = new StationJobId(Guid.NewGuid());
        var artifactContent = "{\"outcome\":\"Passed\"}"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(artifactContent));
        var localKey = $"{jobId.Value:N}/vendor/result.json";
        var localPath = Path.Combine(
            localRoot,
            localKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, artifactContent);
        File.SetAttributes(localPath, File.GetAttributes(localPath) | FileAttributes.ReadOnly);

        var pendingArtifact = new PendingStationJobArtifact(
            "result.json",
            "VendorReport",
            localKey,
            "application/json",
            artifactContent.Length,
            sha256);
        var outbox = CreatePendingCompletion(jobId, pendingArtifact);
        var store = new InMemoryStationJobStore();
        Assert.True(await store.TryAddAsync(CreateAcceptedJob(jobId), Guid.NewGuid(), [outbox]));
        var publisher = new FailOncePublisher();
        var transfer = new FailOnceReleaseTransfer(new FileSystemStationArtifactTransfer(
            new FileSystemStationArtifactTransferOptions(localRoot, exchangeRoot)));
        var clock = new MutableClock(Now);
        var dispatcher = new StationJobOutboxDispatcher(store, publisher, transfer, clock);

        Assert.Equal(0, await dispatcher.DispatchAsync(10));
        Assert.True(File.Exists(localPath));
        var retry = Assert.Single(await store.ListPendingOutboxAsync(10, Now.AddSeconds(1)));
        Assert.Equal(1, retry.AttemptCount);
        Assert.Single(Directory.EnumerateFiles(exchangeRoot, sha256, SearchOption.AllDirectories));

        clock.UtcNow = Now.AddSeconds(1);
        Assert.Equal(1, await dispatcher.DispatchAsync(10));
        Assert.True(File.Exists(localPath));
        Assert.Single(await store.ListPendingArtifactCleanupAsync(10));
        var published = JsonSerializer.Deserialize<StationJobCompleted>(
            publisher.PublishedPayload,
            JsonOptions);
        Assert.NotNull(published);
        var publishedArtifact = Assert.Single(published.Artifacts);
        Assert.Equal($"sha256/{sha256[..2]}/{sha256}", publishedArtifact.StorageKey);
        Assert.Equal(sha256, publishedArtifact.Sha256);

        Assert.Equal(0, await dispatcher.DispatchAsync(10));
        Assert.False(File.Exists(localPath));
        Assert.Empty(await store.ListPendingArtifactCleanupAsync(10));
        Assert.Empty(await store.ListPendingOutboxAsync(10, clock.UtcNow));
        Assert.Equal(2, publisher.AttemptCount);
    }

    [Fact]
    public async Task TransferRejectsTamperedLocalArtifactBeforeExchangePublication()
    {
        var localRoot = Path.Combine(_root, "tampered-local");
        var exchangeRoot = Path.Combine(_root, "tampered-exchange");
        var jobId = new StationJobId(Guid.NewGuid());
        var localKey = $"{jobId.Value:N}/result.csv";
        var path = Path.Combine(localRoot, localKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "tampered");
        var transfer = new FileSystemStationArtifactTransfer(
            new FileSystemStationArtifactTransferOptions(localRoot, exchangeRoot));
        var artifact = new PendingStationJobArtifact(
            "result.csv",
            "VendorReport",
            localKey,
            "text/csv",
            new FileInfo(path).Length,
            new string('a', 64));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await transfer.PublishAsync(jobId, artifact));

        Assert.Empty(Directory.EnumerateFiles(exchangeRoot, "*", SearchOption.AllDirectories));
    }

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

    private static StationJobOutboxMessage CreatePendingCompletion(
        StationJobId jobId,
        PendingStationJobArtifact artifact)
    {
        using var outputs = JsonDocument.Parse("{}");
        var messageId = Guid.NewGuid();
        var completion = new StationJobCompleted(
            messageId,
            jobId.Value,
            "run/operation/1",
            "agent-a",
            "station-a",
            Guid.NewGuid(),
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            outputs.RootElement.Clone(),
            0,
            0,
            0,
            [],
            [],
            [],
            [],
            null,
            null,
            Now);
        return new StationJobOutboxMessage(
            messageId,
            jobId,
            0,
            StationAgentMessageKinds.JobCompletionPendingArtifactTransfer,
            JsonSerializer.Serialize(
                new PendingStationJobCompletion(completion, [artifact]),
                JsonOptions),
            Now,
            0,
            null,
            null);
    }

    private static StationJob CreateAcceptedJob(StationJobId jobId)
    {
        var job = StationJob.Request(new StationJobRequest(
            jobId,
            "run/operation/1",
            "agent-a",
            "station-a",
            "station-system-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new StationOperationRunId("operation-a@1"),
            1,
            "product-a",
            "serialNumber",
            "UNIT-001",
            null,
            null,
            "project-a",
            "application-a",
            "snapshot-a",
            "line-a",
            "topology-a",
            "operator-a",
            new string('b', 64),
            "operation-a",
            "flow-a",
            "flow-a@1",
            "configuration-a",
            "recipe-a",
            [new StationResourceFenceEvidence(
                "Station",
                "station-system-a",
                1,
                Now.AddHours(1))],
            "{}",
            Now));
        job.Accept(Now);
        return job;
    }

    private sealed class FailOncePublisher : IStationAgentMessagePublisher
    {
        public int AttemptCount { get; private set; }

        public string PublishedPayload { get; private set; } = string.Empty;

        public ValueTask PublishAsync(
            string kind,
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttemptCount++;
            if (AttemptCount == 1)
            {
                throw new IOException("Broker is offline.");
            }

            Assert.Equal(StationAgentMessageKinds.JobCompleted, kind);
            PublishedPayload = payloadJson;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceReleaseTransfer(IStationArtifactTransfer inner)
        : IStationArtifactTransfer
    {
        private int _releaseAttempts;

        public ValueTask<StationJobArtifact> PublishAsync(
            StationJobId jobId,
            PendingStationJobArtifact artifact,
            CancellationToken cancellationToken = default) =>
            inner.PublishAsync(jobId, artifact, cancellationToken);

        public ValueTask ReleaseLocalAsync(
            StationJobId jobId,
            PendingStationJobArtifact artifact,
            CancellationToken cancellationToken = default)
        {
            _releaseAttempts++;
            return _releaseAttempts == 1
                ? ValueTask.FromException(new IOException("Local cleanup was interrupted."))
                : inner.ReleaseLocalAsync(jobId, artifact, cancellationToken);
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
