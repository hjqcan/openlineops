using System.Buffers.Binary;
using System.Text.Json;

namespace OpenLineOps.StationRuntime.Contracts;

public static class StationResourceFenceAuthorityWire
{
    private const int MaximumPayloadBytes = 1024 * 1024;

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            message,
            StationOperationDocumentJson.CreateOptions());
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Resource fence authority payload exceeds its protocol limit.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Resource fence authority payload length is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(
                       payload,
                       StationOperationDocumentJson.CreateOptions())
                   ?? throw new InvalidDataException(
                       "Resource fence authority payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Resource fence authority payload is invalid JSON.",
                exception);
        }
    }
}
