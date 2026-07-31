using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class StationControllerHandshakePersistenceJson
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string SerializeSnapshot(
        StationControllerHandshakeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(
            new StationControllerHandshakeSnapshotDocument(
                CurrentSchemaVersion,
                snapshot),
            Options);
    }

    public static StationControllerHandshakeSnapshot DeserializeSnapshot(
        string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new InvalidDataException(
                "Persisted Station controller handshake document cannot be empty.");
        }

        var persisted = JsonSerializer.Deserialize<
                StationControllerHandshakeSnapshotDocument>(document, Options)
            ?? throw new InvalidDataException(
                "Persisted Station controller handshake document has no snapshot.");
        if (persisted.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Station controller handshake schema version "
                + $"{persisted.SchemaVersion} is unsupported.");
        }

        return persisted.Snapshot;
    }

    public static string SerializeFact(StationControllerHandshakeFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return JsonSerializer.Serialize(
            new StationControllerHandshakeFactDocument(
                CurrentSchemaVersion,
                fact),
            Options);
    }

    public static StationControllerHandshakeFact DeserializeFact(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new InvalidDataException(
                "Persisted Station controller handshake fact cannot be empty.");
        }

        var persisted = JsonSerializer.Deserialize<
                StationControllerHandshakeFactDocument>(document, Options)
            ?? throw new InvalidDataException(
                "Persisted Station controller handshake fact has no value.");
        if (persisted.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Station controller handshake fact schema version "
                + $"{persisted.SchemaVersion} is unsupported.");
        }

        return persisted.Fact;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = RuntimePersistenceJson.CreateOptions();
        options.Converters.Add(new JsonStringEnumConverter(
            namingPolicy: null,
            allowIntegerValues: false));
        return options;
    }

    private sealed record StationControllerHandshakeSnapshotDocument(
        int SchemaVersion,
        StationControllerHandshakeSnapshot Snapshot);

    private sealed record StationControllerHandshakeFactDocument(
        int SchemaVersion,
        StationControllerHandshakeFact Fact);
}
