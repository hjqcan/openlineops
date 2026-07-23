using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Agent.Application.StationJobs;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Agent.Domain.StationJobs;
using OpenLineOps.Agent.Infrastructure.Transport;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Infrastructure.Artifacts;

namespace OpenLineOps.Api.Tests;

public sealed class TraceArtifactsApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>, IDisposable
{
    private readonly HttpClient _agentClient;
    private readonly HttpClient _operatorClient;
    private readonly WebApplicationFactory<Program> _configuredFactory;
    private readonly IStationJobCoordinationStore _coordinationStore;
    private readonly string _localRoot;
    private readonly string _storageRoot;

    public TraceArtifactsApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps",
            Guid.NewGuid().ToString("N"));
        _localRoot = Path.Combine(root, "agent-artifacts");
        _storageRoot = Path.Combine(root, "central-trace");
        _configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenLineOps:Traceability:ArtifactStorage:Provider"] =
                        TraceArtifactStorageProviders.FileSystem,
                    ["OpenLineOps:Traceability:ArtifactStorage:RootPath"] = _storageRoot,
                    ["OpenLineOps:Traceability:ArtifactUpload:MaximumArtifactSizeBytes"] = "1048576",
                    ["OpenLineOps:Runtime:Persistence:Provider"] = "InMemory",
                    ["OpenLineOps:Runtime:Coordination:Provider"] = "InMemory",
                    ["OpenLineOps:Runtime:AgentTransport:Provider"] = "Disabled",
                    ["OpenLineOps:Runtime:StationExecution:Provider"] = "InProcess"
                });
            });
        });
        var clientOptions = new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        };
        _agentClient = _configuredFactory.CreateAuthenticatedClient(
            clientOptions,
            ApiTestAuthentication.StationAgentToken);
        _operatorClient = _configuredFactory.CreateAuthenticatedClient(
            clientOptions,
            ApiTestAuthentication.OperatorToken);
        _coordinationStore = _configuredFactory.Services
            .GetRequiredService<IStationJobCoordinationStore>();
    }

    [Fact]
    public async Task AgentUploadIsDurableIdempotentAndOperatorDownloadsExactEvidence()
    {
        var payload = Encoding.UTF8.GetBytes("artifact upload payload");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        var jobId = new StationJobId(Guid.NewGuid());
        var localKey = $"{jobId.Value:N}/vision.log";
        var localPath = Path.Combine(
            _localRoot,
            localKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, payload);
        File.SetAttributes(localPath, File.GetAttributes(localPath) | FileAttributes.ReadOnly);
        var pending = new PendingStationJobArtifact(
            "vision.log",
            "VendorLog",
            localKey,
            "text/plain",
            payload.LongLength,
            sha256);
        await PrimeJobAsync(jobId.Value);
        var transfer = CreateTransfer(
            ApiTestAuthentication.StationAgentActorId,
            ApiTestAuthentication.StationAgentStationId);

        var first = await transfer.PublishAsync(jobId, pending);
        var replay = await transfer.PublishAsync(jobId, pending);

        Assert.Equal(first, replay);
        var expected = StationArtifactReceiptIdentity.Create(
            ApiTestAuthentication.StationAgentActorId,
            ApiTestAuthentication.StationAgentStationId,
            jobId.Value,
            pending.Name,
            pending.Kind,
            pending.MediaType,
            pending.SizeBytes,
            pending.Sha256);
        Assert.Equal(expected.StorageKey, first.StorageKey);
        Assert.Equal(expected.ReceiptId, first.ReceiptId);

        using var downloadResponse = await _operatorClient.GetAsync(
            $"/api/traceability/artifacts/{first.StorageKey}");
        var downloadedPayload = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("text/plain", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload, downloadedPayload);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(downloadedPayload)));
    }

    [Fact]
    public async Task AgentCannotUploadForAnotherStationIdentity()
    {
        var jobId = Guid.NewGuid();
        using var request = CreateRawUpload(
            jobId,
            ApiTestAuthentication.StationAgentActorId,
            "station.other",
            "result.json",
            "application/json",
            "{}"u8.ToArray());

        using var response = await _agentClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UploadRejectsMultipartChunkedAndContentBeyondDeclaredHash()
    {
        using var multipart = new MultipartFormDataContent
        {
            { new ByteArrayContent("legacy"u8.ToArray()), "file", "legacy.txt" }
        };
        using var multipartResponse = await _agentClient.PostAsync(
            "/api/traceability/artifacts",
            multipart);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, multipartResponse.StatusCode);

        using var chunked = CreateRawUpload(
            Guid.NewGuid(),
            ApiTestAuthentication.StationAgentActorId,
            ApiTestAuthentication.StationAgentStationId,
            "chunked.bin",
            null,
            "chunked"u8.ToArray());
        chunked.Content!.Headers.ContentLength = null;
        chunked.Headers.TransferEncodingChunked = true;
        using var chunkedResponse = await _agentClient.SendAsync(chunked);
        Assert.Equal(HttpStatusCode.BadRequest, chunkedResponse.StatusCode);

        var expected = "expected"u8.ToArray();
        var mismatchedJobId = Guid.NewGuid();
        await PrimeJobAsync(mismatchedJobId);
        using var mismatched = CreateRawUpload(
            mismatchedJobId,
            ApiTestAuthentication.StationAgentActorId,
            ApiTestAuthentication.StationAgentStationId,
            "mismatch.bin",
            null,
            "tampered"u8.ToArray(),
            Convert.ToHexStringLower(SHA256.HashData(expected)));
        using var mismatchedResponse = await _agentClient.SendAsync(mismatched);
        Assert.Equal(HttpStatusCode.BadRequest, mismatchedResponse.StatusCode);

        var uppercaseHashJobId = Guid.NewGuid();
        await PrimeJobAsync(uppercaseHashJobId);
        var uppercaseHashContent = "uppercase hash"u8.ToArray();
        using var uppercaseHash = CreateRawUpload(
            uppercaseHashJobId,
            ApiTestAuthentication.StationAgentActorId,
            ApiTestAuthentication.StationAgentStationId,
            "uppercase-hash.bin",
            null,
            uppercaseHashContent,
            Convert.ToHexString(SHA256.HashData(uppercaseHashContent)));
        using var uppercaseHashResponse = await _agentClient.SendAsync(uppercaseHash);
        Assert.Equal(HttpStatusCode.BadRequest, uppercaseHashResponse.StatusCode);
    }

    [Fact]
    public async Task UploadRequiresExactActiveJobOwnership()
    {
        using var missing = CreateRawUpload(
            Guid.NewGuid(),
            ApiTestAuthentication.StationAgentActorId,
            ApiTestAuthentication.StationAgentStationId,
            "missing.json",
            "application/json",
            "{}"u8.ToArray());
        using var missingResponse = await _agentClient.SendAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);

        var foreignJobId = Guid.NewGuid();
        await PrimeJobAsync(foreignJobId, "agent.other", "station.other");
        using var foreign = CreateRawUpload(
            foreignJobId,
            ApiTestAuthentication.StationAgentActorId,
            ApiTestAuthentication.StationAgentStationId,
            "foreign.json",
            "application/json",
            "{}"u8.ToArray());
        using var foreignResponse = await _agentClient.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task TerminalJobAllowsOnlyExactArtifactReplay()
    {
        var payload = "terminal evidence"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        var jobId = Guid.NewGuid();
        var request = await PrimeJobAsync(jobId);
        var localKey = $"{jobId:N}/terminal.txt";
        var localPath = Path.Combine(
            _localRoot,
            localKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, payload);
        File.SetAttributes(localPath, File.GetAttributes(localPath) | FileAttributes.ReadOnly);
        var pending = new PendingStationJobArtifact(
            "terminal.txt",
            "VendorReport",
            localKey,
            "text/plain",
            payload.LongLength,
            sha256);
        var transfer = CreateTransfer(request.AgentId, request.StationId);
        var receipt = await transfer.PublishAsync(new StationJobId(jobId), pending);
        await _coordinationStore.RecordAcceptedAsync(new StationJobAccepted(
            Guid.NewGuid(),
            jobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.RequestedAtUtc.AddSeconds(1)));
        await _coordinationStore.RecordCompletionAsync(new StationJobCompleted(
            Guid.NewGuid(),
            jobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.RuntimeSessionId,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            JsonSerializer.SerializeToElement(new { }),
            0,
            0,
            0,
            [],
            [],
            [],
            [new StationJobArtifact(
                receipt.Name,
                receipt.Kind,
                receipt.StorageKey,
                receipt.ReceiptId,
                receipt.MediaType,
                receipt.SizeBytes,
                receipt.Sha256)],
            null,
            null,
            request.RequestedAtUtc.AddSeconds(2)));

        Assert.Equal(receipt, await transfer.PublishAsync(new StationJobId(jobId), pending));

        using var changedMetadata = CreateRawUpload(
            jobId,
            request.AgentId,
            request.StationId,
            pending.Name,
            pending.MediaType,
            payload,
            artifactKind: "DifferentKind");
        using var changedResponse = await _agentClient.SendAsync(changedMetadata);
        Assert.Equal(HttpStatusCode.Conflict, changedResponse.StatusCode);
    }

    [Fact]
    public async Task RecoveryRequiredJobAllowsOnlyPreviouslyReceivedArtifactReplay()
    {
        var payload = "recovery evidence"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        var jobId = Guid.NewGuid();
        var request = await PrimeJobAsync(jobId);
        var localKey = $"{jobId:N}/recovery.txt";
        var localPath = Path.Combine(
            _localRoot,
            localKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllBytesAsync(localPath, payload);
        File.SetAttributes(localPath, File.GetAttributes(localPath) | FileAttributes.ReadOnly);
        var pending = new PendingStationJobArtifact(
            "recovery.txt",
            "VendorReport",
            localKey,
            "text/plain",
            payload.LongLength,
            sha256);
        var transfer = CreateTransfer(request.AgentId, request.StationId);
        var received = await transfer.PublishAsync(new StationJobId(jobId), pending);
        await _coordinationStore.RecordAcceptedAsync(new StationJobAccepted(
            Guid.NewGuid(),
            jobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.RequestedAtUtc.AddSeconds(1)));
        await _coordinationStore.RecordRecoveryRequiredAsync(new StationJobRecoveryRequired(
            Guid.NewGuid(),
            $"recovery-required/{jobId:D}",
            jobId,
            request.IdempotencyKey,
            request.AgentId,
            request.StationId,
            request.ProductionRunId,
            request.OperationRunId,
            request.RuntimeSessionId,
            "The non-idempotent hardware action requires reconciliation.",
            request.RequestedAtUtc.AddSeconds(2)));

        Assert.Equal(received, await transfer.PublishAsync(new StationJobId(jobId), pending));

        using var changedMetadata = CreateRawUpload(
            jobId,
            request.AgentId,
            request.StationId,
            pending.Name,
            pending.MediaType,
            payload,
            artifactKind: "DifferentKind");
        using var changedResponse = await _agentClient.SendAsync(changedMetadata);
        Assert.Equal(HttpStatusCode.Conflict, changedResponse.StatusCode);
    }

    [Theory]
    [InlineData(StationArtifactUploadProtocol.ArtifactNameHeader, "%76ision.log")]
    [InlineData(StationArtifactUploadProtocol.ArtifactKindHeader, "%56endorReport")]
    [InlineData(StationArtifactUploadProtocol.ArtifactMediaTypeHeader, "%74ext%2Fplain")]
    public async Task UploadRejectsNonCanonicalEncodedHeaders(
        string headerName,
        string nonCanonicalValue)
    {
        var jobId = Guid.NewGuid();
        await PrimeJobAsync(jobId);
        using var request = CreateRawUpload(
            jobId,
            ApiTestAuthentication.StationAgentActorId,
            ApiTestAuthentication.StationAgentStationId,
            "vision.log",
            "text/plain",
            "evidence"u8.ToArray());
        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(
            headerName,
            nonCanonicalValue);

        using var response = await _agentClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpStationArtifactTransfer CreateTransfer(string agentId, string stationId) => new(
        new HttpStationArtifactTransferOptions(
            _localRoot,
            _agentClient.BaseAddress!,
            ApiTestAuthentication.StationAgentToken,
            agentId,
            stationId,
            TimeSpan.FromSeconds(30)),
        _agentClient);

    private static HttpRequestMessage CreateRawUpload(
        Guid jobId,
        string agentId,
        string stationId,
        string artifactName,
        string? mediaType,
        byte[] content,
        string? declaredSha256 = null,
        string artifactKind = "VendorReport")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/traceability/artifacts")
        {
            Content = new ByteArrayContent(content)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/octet-stream");
        request.Headers.Add(StationArtifactUploadProtocol.AgentIdHeader, agentId);
        request.Headers.Add(StationArtifactUploadProtocol.StationIdHeader, stationId);
        request.Headers.Add(StationArtifactUploadProtocol.JobIdHeader, jobId.ToString("D"));
        request.Headers.Add(
            StationArtifactUploadProtocol.ArtifactNameHeader,
            StationArtifactUploadProtocol.EncodeArtifactName(artifactName));
        request.Headers.Add(
            StationArtifactUploadProtocol.ArtifactKindHeader,
            StationArtifactUploadProtocol.EncodeArtifactKind(artifactKind));
        request.Headers.Add(
            StationArtifactUploadProtocol.ArtifactSizeHeader,
            content.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(
            StationArtifactUploadProtocol.ArtifactSha256Header,
            declaredSha256 ?? Convert.ToHexStringLower(SHA256.HashData(content)));
        if (mediaType is not null)
        {
            request.Headers.Add(
                StationArtifactUploadProtocol.ArtifactMediaTypeHeader,
                StationArtifactUploadProtocol.EncodeMediaType(mediaType));
        }

        return request;
    }

    private async ValueTask<StationJobRequested> PrimeJobAsync(
        Guid jobId,
        string agentId = ApiTestAuthentication.StationAgentActorId,
        string stationId = ApiTestAuthentication.StationAgentStationId)
    {
        var requestedAtUtc = DateTimeOffset.UtcNow;
        const string stationSystemId = "system.station.trace-upload";
        var fence = new StationResourceFence(
            "Station",
            stationSystemId,
            1,
            requestedAtUtc.AddMinutes(5));
        var request = new StationJobRequested(
            Guid.NewGuid(),
            jobId,
            $"trace-upload/{jobId:D}",
            agentId,
            stationId,
            stationSystemId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"operation-run-{jobId:N}",
            1,
            "product-model.trace",
            "serialNumber",
            $"unit-{jobId:N}",
            null,
            null,
            "project.trace",
            "application.trace",
            "snapshot.trace",
            "line.trace",
            "topology.trace",
            "actor.trace",
            new string('a', 64),
            "operation.trace",
            "flow.trace",
            "flow-version.trace",
            "configuration.trace",
            "recipe.trace",
            [fence],
            JsonSerializer.SerializeToElement(new { }),
            requestedAtUtc);
        Assert.True(await _coordinationStore.TryEnqueueAsync(
            request,
            [StationDispatchMessageIdentity.CreateLeaseGranted(request, fence)]));
        return request;
    }

    public void Dispose()
    {
        _agentClient.Dispose();
        _operatorClient.Dispose();
        _configuredFactory.Dispose();
        if (!Directory.Exists(Path.GetDirectoryName(_localRoot)))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     Path.GetDirectoryName(_localRoot)!,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(Path.GetDirectoryName(_localRoot)!, recursive: true);
    }
}
