using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class StationLifecyclePersistenceJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(StationLifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, Options);
    }

    public static StationLifecycleSnapshot Deserialize(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new InvalidDataException(
                "Persisted Station lifecycle document cannot be empty.");
        }

        return JsonSerializer.Deserialize<StationLifecycleSnapshot>(document, Options)
            ?? throw new InvalidDataException(
                "Persisted Station lifecycle document has no snapshot.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = RuntimePersistenceJson.CreateOptions();
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null,
            allowIntegerValues: false));
        return options;
    }
}
