using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenLineOps.Traceability.Infrastructure.Artifacts;

namespace OpenLineOps.Api.Tests;

public sealed class TraceArtifactsApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _storageRoot;

    public TraceArtifactsApiTests(WebApplicationFactory<Program> factory)
    {
        _storageRoot = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps",
            Guid.NewGuid().ToString("N"));
        _client = factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                {
                    configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["OpenLineOps:Traceability:ArtifactStorage:Provider"] =
                            TraceArtifactStorageProviders.FileSystem,
                        ["OpenLineOps:Traceability:ArtifactStorage:RootPath"] = _storageRoot
                    });
                });
            })
            .CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
    }

    [Fact]
    public async Task StoreArtifactReturnsMetadataAndDownloadReturnsContent()
    {
        var payload = Encoding.UTF8.GetBytes("artifact upload payload");
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(payload);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "file", "vision.log");

        using var response = await _client.PostAsync("/api/traceability/artifacts", form);
        using var body = await ReadJsonAsync(response);
        var storageKey = body.RootElement.GetProperty("storageKey").GetString()!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.EndsWith(".log", storageKey, StringComparison.Ordinal);
        Assert.Equal("vision.log", body.RootElement.GetProperty("fileName").GetString());
        Assert.Equal(payload.Length, body.RootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            body.RootElement.GetProperty("sha256").GetString());

        using var downloadResponse = await _client.GetAsync($"/api/traceability/artifacts/{storageKey}");
        var downloadedPayload = await downloadResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("text/plain", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload, downloadedPayload);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    public void Dispose()
    {
        _client.Dispose();

        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }
}
