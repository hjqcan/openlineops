using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Traceability.Application.Artifacts;
using OpenLineOps.Traceability.Infrastructure.Artifacts;

namespace OpenLineOps.Traceability.Tests;

public sealed class FileSystemTraceArtifactStorageTests
{
    [Fact]
    public async Task StoreAsyncWritesArtifactAndOpenReadAsyncReturnsContent()
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new FileSystemTraceArtifactStorage(directory.Path);
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
        Assert.Equal(ComputeSha256(contentBytes), readResult.Value.Sha256);
        Assert.Equal("inspection artifact payload", storedText);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task StoreAsyncRejectsExplicitBlankExpectedSha256(string expectedSha256)
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new FileSystemTraceArtifactStorage(directory.Path);

        var result = await storage.StoreAsync(new StoreTraceArtifactRequest(
            "trace/blank.log",
            "blank.log",
            "text/plain",
            new MemoryStream([1, 2, 3]),
            expectedSha256));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.ArtifactSha256Invalid", result.Error.Code);
    }

    [Fact]
    public async Task StoreAsyncRejectsUppercaseExpectedSha256Alias()
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new FileSystemTraceArtifactStorage(directory.Path);
        var bytes = Encoding.UTF8.GetBytes("canonical digest");

        var result = await storage.StoreAsync(new StoreTraceArtifactRequest(
            "trace/uppercase.log",
            "uppercase.log",
            "text/plain",
            new MemoryStream(bytes),
            ComputeSha256(bytes).ToUpperInvariant()));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.ArtifactSha256Invalid", result.Error.Code);
    }

    [Fact]
    public async Task StoreAsyncRejectsRelativeStorageKey()
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new FileSystemTraceArtifactStorage(directory.Path);

        var result = await storage.StoreAsync(new StoreTraceArtifactRequest(
            "../escape.log",
            "escape.log",
            "text/plain",
            new MemoryStream([1, 2, 3])));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.ArtifactStorageKeyInvalid", result.Error.Code);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "..", "escape.log")));
    }

    [Theory]
    [InlineData(" trace/file.log", "text/plain")]
    [InlineData("trace/file.log", " text/plain")]
    public async Task StoreAsyncRejectsNonCanonicalTextInsteadOfNormalizingIt(
        string storageKey,
        string mediaType)
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new FileSystemTraceArtifactStorage(directory.Path);

        var result = await storage.StoreAsync(new StoreTraceArtifactRequest(
            storageKey,
            "file.log",
            mediaType,
            new MemoryStream([1, 2, 3])));

        Assert.True(result.IsFailure);
        Assert.StartsWith("Validation.Traceability.Artifact", result.Error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreAsyncIsIdempotentForExactContentAndConflictsForDifferentContent()
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new FileSystemTraceArtifactStorage(directory.Path);
        var original = "original immutable evidence"u8.ToArray();
        var replacement = "different immutable evidence"u8.ToArray();
        const string storageKey = "station/job/artifact.bin";

        var first = await storage.StoreAsync(new StoreTraceArtifactRequest(
            storageKey,
            "artifact.bin",
            "application/octet-stream",
            new MemoryStream(original),
            ComputeSha256(original),
            original.LongLength));
        var replay = await storage.StoreAsync(new StoreTraceArtifactRequest(
            storageKey,
            "artifact.bin",
            "application/octet-stream",
            new MemoryStream(original),
            ComputeSha256(original),
            original.LongLength));
        var conflict = await storage.StoreAsync(new StoreTraceArtifactRequest(
            storageKey,
            "artifact.bin",
            "application/octet-stream",
            new MemoryStream(replacement),
            ComputeSha256(replacement),
            replacement.LongLength));

        Assert.True(first.IsSuccess, first.Error.Message);
        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.Equal(first.Value, replay.Value);
        Assert.True(conflict.IsFailure);
        Assert.Equal("Conflict.Traceability.ArtifactStorageKeyConflict", conflict.Error.Code);
        Assert.True(new FileInfo(Path.Combine(directory.Path, "station/job/artifact.bin"))
            .IsReadOnly);
    }

    [Fact]
    public async Task StoreAsyncEnforcesMaximumForDeclaredAndStreamingContent()
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new FileSystemTraceArtifactStorage(directory.Path, maximumArtifactSizeBytes: 4);

        var declared = await storage.StoreAsync(new StoreTraceArtifactRequest(
            "too-large/declared.bin",
            "declared.bin",
            null,
            new MemoryStream(new byte[5]),
            ExpectedSizeBytes: 5));
        var streamed = await storage.StoreAsync(new StoreTraceArtifactRequest(
            "too-large/streamed.bin",
            "streamed.bin",
            null,
            new MemoryStream(new byte[5])));

        Assert.Equal(
            "Validation.Traceability.ArtifactSizeLimitExceeded",
            declared.Error.Code);
        Assert.Equal(
            "Validation.Traceability.ArtifactSizeLimitExceeded",
            streamed.Error.Code);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("reports/CON.json")]
    [InlineData("reports/com1.txt")]
    [InlineData("reports/name./artifact.bin")]
    public async Task StoreAsyncRejectsReservedFilesystemSegments(string storageKey)
    {
        using var directory = TemporaryDirectory.Create();
        var storage = new FileSystemTraceArtifactStorage(directory.Path);

        var result = await storage.StoreAsync(new StoreTraceArtifactRequest(
            storageKey,
            "artifact.bin",
            null,
            new MemoryStream([1])));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.ArtifactStorageKeyInvalid", result.Error.Code);
    }

    [Fact]
    public async Task StoreAsyncRejectsReparseDirectoryBeforeWritingOutsideRoot()
    {
        using var root = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var link = Path.Combine(root.Path, "redirect");
        if (!TryCreateDirectoryLink(link, outside.Path))
        {
            return;
        }

        var storage = new FileSystemTraceArtifactStorage(root.Path);
        var result = await storage.StoreAsync(new StoreTraceArtifactRequest(
            "redirect/escaped.bin",
            "escaped.bin",
            null,
            new MemoryStream([1, 2, 3])));

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Traceability.ArtifactPathUnsafe", result.Error.Code);
        Assert.False(File.Exists(Path.Combine(outside.Path, "escaped.bin")));
    }

    [Fact]
    public void ConstructorRejectsReparsePointInRootAncestorChain()
    {
        using var links = TemporaryDirectory.Create();
        using var target = TemporaryDirectory.Create();
        var linkedParent = Path.Combine(links.Path, "linked-parent");
        if (!TryCreateDirectoryLink(linkedParent, target.Path))
        {
            return;
        }

        var rootThroughLink = Path.Combine(linkedParent, "artifacts");

        Assert.Throws<InvalidDataException>(() =>
            new FileSystemTraceArtifactStorage(rootThroughLink));
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                            or IOException
                                            or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             Path,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
                }

                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
