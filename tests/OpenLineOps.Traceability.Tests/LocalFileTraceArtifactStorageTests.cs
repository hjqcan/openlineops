using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Traceability.Application.Artifacts;
using OpenLineOps.Traceability.Infrastructure.Artifacts;

namespace OpenLineOps.Traceability.Tests;

public sealed class LocalFileTraceArtifactStorageTests
{
    [Fact]
    public async Task StoreAsyncWritesArtifactAndOpenReadAsyncReturnsContent()
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new LocalFileTraceArtifactStorage(directory.Path);
        var contentBytes = Encoding.UTF8.GetBytes("inspection artifact payload");

        var storeResult = await storage.StoreAsync(new StoreTraceArtifactRequest(
            "trace/SMX-1001/vision.log",
            "vision.log",
            "text/plain",
            new MemoryStream(contentBytes),
            ComputeSha256(contentBytes)));

        Assert.True(storeResult.IsSuccess, storeResult.Error.Message);
        Assert.Equal("trace/SMX-1001/vision.log", storeResult.Value.StorageKey);
        Assert.Equal("vision.log", storeResult.Value.FileName);
        Assert.Equal(contentBytes.Length, storeResult.Value.SizeBytes);
        Assert.Equal(ComputeSha256(contentBytes), storeResult.Value.Sha256);

        var readResult = await storage.OpenReadAsync(storeResult.Value.StorageKey);
        Assert.True(readResult.IsSuccess, readResult.Error.Message);

        await using var stream = readResult.Value.Content;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var storedText = await reader.ReadToEndAsync();

        Assert.Equal("text/plain", readResult.Value.MediaType);
        Assert.Equal("inspection artifact payload", storedText);
    }

    [Fact]
    public async Task StoreAsyncRejectsRelativeStorageKey()
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new LocalFileTraceArtifactStorage(directory.Path);

        var result = await storage.StoreAsync(new StoreTraceArtifactRequest(
            "../escape.log",
            "escape.log",
            "text/plain",
            new MemoryStream([1, 2, 3])));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.ArtifactStorageKeyInvalid", result.Error.Code);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "..", "escape.log")));
    }

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenLineOps",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);

            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
