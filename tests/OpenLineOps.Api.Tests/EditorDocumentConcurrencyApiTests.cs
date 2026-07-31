using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Tests;

public sealed class EditorDocumentConcurrencyApiTests : IDisposable
{
    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-editor-concurrency-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TopologyMutationRequiresRevisionRejectsStaleAndAllowsExplicitOverwrite()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-{suffix}";
        var applicationId = $"application-{suffix}";
        var topologyId = $"topology-{suffix}";
        var topologyPath = $"/api/automation-projects/{projectId}/applications/{applicationId}/topologies/{topologyId}";
        using var factory = ApiTestAuthentication.CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var workspace = await client.PostAsJsonAsync("/api/automation-project-workspaces", new
        {
            projectId,
            displayName = "Editor Concurrency",
            projectPath = _projectDirectory,
            defaultApplicationId = applicationId,
            defaultApplicationName = "Main"
        });
        Assert.Equal(HttpStatusCode.Created, workspace.StatusCode);
        using var created = await client.PostAsJsonAsync(
            $"/api/automation-projects/{projectId}/applications/{applicationId}/topologies",
            new { topologyId, displayName = "Topology" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = await ReadJsonAsync(created);
        var loadedRevision = createdBody.RootElement.GetProperty("revision").GetString();
        Assert.NotNull(loadedRevision);
        Assert.Equal($"\"{loadedRevision}\"", created.Headers.ETag?.Tag);

        var mutationPath = $"{topologyPath}/systems";
        var firstNode = Node("station.one", "Station One");
        using var missing = await client.PostAsJsonAsync(mutationPath, firstNode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, mutationPath)
        {
            Content = JsonContent.Create(firstNode)
        };
        firstRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{loadedRevision}\"");
        using var first = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstBody = await ReadJsonAsync(first);
        var currentRevision = firstBody.RootElement.GetProperty("revision").GetString();
        Assert.NotEqual(loadedRevision, currentRevision);

        using var staleRequest = new HttpRequestMessage(HttpMethod.Post, mutationPath)
        {
            Content = JsonContent.Create(Node("station.stale", "Stale Station"))
        };
        staleRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{loadedRevision}\"");
        using var stale = await client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var staleBody = await ReadJsonAsync(stale);
        Assert.Equal(
            currentRevision,
            staleBody.RootElement.GetProperty("currentRevision").GetString());

        using var forceWithoutIntent = new HttpRequestMessage(HttpMethod.Post, mutationPath)
        {
            Content = JsonContent.Create(Node("station.force", "Forced Station"))
        };
        forceWithoutIntent.Headers.TryAddWithoutValidation("If-Match", "*");
        using var forceRejected = await client.SendAsync(forceWithoutIntent);
        Assert.Equal(HttpStatusCode.BadRequest, forceRejected.StatusCode);

        using var forceRequest = new HttpRequestMessage(HttpMethod.Post, mutationPath)
        {
            Content = JsonContent.Create(Node("station.force", "Forced Station"))
        };
        forceRequest.Headers.TryAddWithoutValidation("If-Match", "*");
        forceRequest.Headers.TryAddWithoutValidation(
            EditorDocumentConcurrency.ConflictResolutionHeaderName,
            EditorDocumentConcurrency.ExplicitOverwriteToken);
        using var forced = await client.SendAsync(forceRequest);
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);

        using var final = await client.GetAsync(topologyPath);
        using var finalBody = await ReadJsonAsync(final);
        var ids = finalBody.RootElement.GetProperty("systems").EnumerateArray()
            .Select(item => item.GetProperty("systemId").GetString())
            .ToArray();
        Assert.Contains("station.one", ids);
        Assert.DoesNotContain("station.stale", ids);
        Assert.Contains("station.force", ids);
    }

    [Fact]
    public void RevisionPreconditionTokensAreExact()
    {
        var revision = EditorDocumentConcurrency.ComputeRevision(new { id = "line", value = 1 });
        Assert.Equal(64, revision.Length);
        Assert.Equal(
            EditorDocumentPrecondition.Satisfied,
            EditorDocumentConcurrency.Evaluate($"\"{revision}\"", null, revision));
        Assert.Equal(
            EditorDocumentPrecondition.Stale,
            EditorDocumentConcurrency.Evaluate($"\"{revision.ToUpperInvariant()}\"", null, revision));
        Assert.Equal(
            EditorDocumentPrecondition.ForceNotExplicit,
            EditorDocumentConcurrency.Evaluate("*", null, revision));
    }

    [Fact]
    public async Task DocumentGateSerializesWritersAndRetiresCanceledWaiters()
    {
        var key = $"editor-gate-{Guid.NewGuid():N}";
        await using var first = await EditorDocumentConcurrency.AcquireAsync(
            key,
            CancellationToken.None);
        var secondTask = EditorDocumentConcurrency.AcquireAsync(
                key,
                CancellationToken.None)
            .AsTask();
        await Task.Yield();
        Assert.False(secondTask.IsCompleted);

        using var canceled = new CancellationTokenSource();
        var canceledTask = EditorDocumentConcurrency.AcquireAsync(key, canceled.Token).AsTask();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledTask);

        await first.DisposeAsync();
        await using (var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            Assert.NotNull(second);
        }

        await using var afterRetirement = await EditorDocumentConcurrency.AcquireAsync(
            key,
            CancellationToken.None);
        Assert.NotNull(afterRetirement);
    }

    private static object Node(string systemId, string displayName) => new
    {
        systemId,
        parentSystemId = (string?)null,
        kind = "Station",
        systemType = "test.station",
        displayName,
        requiredCapabilityIds = Array.Empty<string>(),
        providedCapabilityIds = Array.Empty<string>(),
        metadata = new Dictionary<string, string>()
    };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDirectory))
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
    }
}
