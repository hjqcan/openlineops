using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Processes.Infrastructure.Persistence;

namespace OpenLineOps.Processes.Tests;

public sealed class ProjectProcessResourceFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-process-resource-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AtomicReplacementSucceedsWhileExistingResourceIsBeingRead()
    {
        var path = Path.Combine(_root, "flow.json");
        await ProjectProcessResourceFileStore.SaveJsonAsync(
            path,
            new BlockingReadDocument("before"),
            CancellationToken.None);

        BlockingReadDocument.BeginBlockingRead();
        var readTask = Task.Run(async () =>
            await ProjectProcessResourceFileStore
                .LoadJsonAsync<BlockingReadDocument>(path, CancellationToken.None));

        Assert.True(
            BlockingReadDocument.ReadStarted.Wait(TimeSpan.FromSeconds(10)),
            "The project resource reader did not enter JSON deserialization.");

        try
        {
            await ProjectProcessResourceFileStore.SaveJsonAsync(
                path,
                new BlockingReadDocument("after"),
                CancellationToken.None);
        }
        finally
        {
            BlockingReadDocument.AllowReadToFinish.Set();
        }

        var original = await readTask;
        var replacement = await ProjectProcessResourceFileStore
            .LoadJsonAsync<BlockingReadDocument>(path, CancellationToken.None);

        Assert.Equal("before", original?.Value);
        Assert.Equal("after", replacement?.Value);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        BlockingReadDocument.AllowReadToFinish.Set();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [JsonConverter(typeof(BlockingReadDocumentConverter))]
    private sealed record BlockingReadDocument(string Value)
    {
        public static ManualResetEventSlim ReadStarted { get; } = new(initialState: false);

        public static ManualResetEventSlim AllowReadToFinish { get; } = new(initialState: true);

        public static void BeginBlockingRead()
        {
            ReadStarted.Reset();
            AllowReadToFinish.Reset();
        }
    }

    private sealed class BlockingReadDocumentConverter : JsonConverter<BlockingReadDocument>
    {
        public override BlockingReadDocument Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement.GetProperty("value").GetString()
                ?? throw new JsonException("The blocking read document value is required.");

            BlockingReadDocument.ReadStarted.Set();
            if (!BlockingReadDocument.AllowReadToFinish.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The blocking project resource read was not released.");
            }

            return new BlockingReadDocument(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            BlockingReadDocument value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("value", value.Value);
            writer.WriteEndObject();
        }
    }
}
