using System.Net.Http.Json;
using System.Text.Json;

namespace OpenLineOps.Api.Tests;

internal static class EditorDocumentHttpTestExtensions
{
    public static Task<HttpResponseMessage> PostEditorAsync<T>(
        this HttpClient client,
        string documentPath,
        string requestPath,
        T body) => SendEditorAsync(client, HttpMethod.Post, documentPath, requestPath, body);

    public static Task<HttpResponseMessage> PutEditorAsync<T>(
        this HttpClient client,
        string documentPath,
        string requestPath,
        T body) => SendEditorAsync(client, HttpMethod.Put, documentPath, requestPath, body);

    public static Task<HttpResponseMessage> PatchEditorAsync<T>(
        this HttpClient client,
        string documentPath,
        string requestPath,
        T body) => SendEditorAsync(client, HttpMethod.Patch, documentPath, requestPath, body);

    public static async Task<HttpResponseMessage> DeleteEditorAsync(
        this HttpClient client,
        string documentPath,
        string requestPath)
    {
        return await SendEditorAsync<object?>(client, HttpMethod.Delete, documentPath, requestPath, null)
            .ConfigureAwait(false);
    }

    public static async Task<HttpRequestMessage> CreateEditorRequestAsync<T>(
        this HttpClient client,
        HttpMethod method,
        string documentPath,
        string requestPath,
        T? body)
    {
        using var current = await client.GetAsync(documentPath).ConfigureAwait(false);
        current.EnsureSuccessStatusCode();
        await using var stream = await current.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var revision = document.RootElement.GetProperty("revision").GetString();
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new InvalidOperationException($"Editor document {documentPath} did not return a revision.");
        }

        var request = new HttpRequestMessage(method, requestPath);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }

    private static async Task<HttpResponseMessage> SendEditorAsync<T>(
        HttpClient client,
        HttpMethod method,
        string documentPath,
        string requestPath,
        T? body)
    {
        using var request = await client
            .CreateEditorRequestAsync(method, documentPath, requestPath, body)
            .ConfigureAwait(false);
        return await client.SendAsync(request).ConfigureAwait(false);
    }
}
