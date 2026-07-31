using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.BuiltinPlugins.DeviceSessions;

public static class DeviceSessionJournal
{
    internal const string SchemaVersion = "1.0";
    internal const string CommandRequestKind = "command.request";
    internal const string CommandResponseKind = "command.response";
    internal const string SignalSampleKind = "signal.sample";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async ValueTask<IReadOnlyList<DeviceSessionJournalEntry>>
        ReadAndVerifyAsync(
            string path,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Journal path must be absolute.", nameof(path));
        }

        var entries = new List<DeviceSessionJournalEntry>();
        string? previousHash = null;
        long expectedSequence = 1;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16_384,
            leaveOpen: false);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
               is { } line)
        {
            if (line.Length == 0)
            {
                throw new InvalidDataException("Journal cannot contain blank lines.");
            }

            DeviceSessionJournalLine envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<DeviceSessionJournalLine>(
                        line,
                        JsonOptions)
                    ?? throw new InvalidDataException("Journal line cannot be null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Journal line {expectedSequence} is invalid JSON.",
                    exception);
            }

            var entry = envelope.Event
                ?? throw new InvalidDataException(
                    $"Journal line {expectedSequence} has no event.");
            ValidateEvent(entry, expectedSequence, previousHash);
            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            var actualHash = Convert.ToHexString(SHA256.HashData(canonicalBytes))
                .ToLowerInvariant();
            if (!string.Equals(actualHash, envelope.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Journal line {expectedSequence} SHA-256 verification failed.");
            }

            entries.Add(entry);
            previousHash = actualHash;
            expectedSequence++;
        }

        return entries.AsReadOnly();
    }

    internal static async ValueTask<DeviceSessionJournalWriter?> OpenWriterAsync(
        RecordingConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        return configuration is null
            ? null
            : await DeviceSessionJournalWriter.OpenAsync(
                    configuration.JournalPath,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    internal static string SerializePayload<T>(T payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    internal static T DeserializePayload<T>(string? payloadJson, string description)
    {
        if (payloadJson is null)
        {
            throw new InvalidDataException($"{description} payload is missing.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payloadJson, JsonOptions)
                ?? throw new InvalidDataException($"{description} payload cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{description} payload is invalid.",
                exception);
        }
    }

    private static void ValidateEvent(
        DeviceSessionJournalEntry entry,
        long expectedSequence,
        string? expectedPreviousHash)
    {
        if (!string.Equals(entry.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Journal line {expectedSequence} uses unsupported schema '{entry.SchemaVersion}'.");
        }

        if (entry.JournalSequence != expectedSequence)
        {
            throw new InvalidDataException(
                $"Journal sequence {entry.JournalSequence} is out of order; expected {expectedSequence}.");
        }

        if (!string.Equals(
                entry.PreviousSha256,
                expectedPreviousHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Journal line {expectedSequence} does not continue the SHA-256 chain.");
        }

        if (entry.RecordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"Journal line {expectedSequence} timestamp must be UTC.");
        }

        if (entry.Kind is not (CommandRequestKind or CommandResponseKind or SignalSampleKind))
        {
            throw new InvalidDataException(
                $"Journal line {expectedSequence} kind '{entry.Kind}' is unsupported.");
        }

        if (entry.DeviceSequence < 0)
        {
            throw new InvalidDataException(
                $"Journal line {expectedSequence} device sequence cannot be negative.");
        }
    }
}

public sealed record DeviceSessionJournalEntry(
    string SchemaVersion,
    long JournalSequence,
    string Kind,
    DateTimeOffset RecordedAtUtc,
    string DeviceInstanceId,
    string? CorrelationId,
    long? DeviceSequence,
    string? SignalId,
    DateTimeOffset? SourceTimestampUtc,
    DateTimeOffset? ObservedTimestampUtc,
    string? PayloadJson,
    string? PreviousSha256);

internal sealed record DeviceSessionJournalLine(
    DeviceSessionJournalEntry Event,
    string Sha256);

internal sealed record JournalCommandRequestPayload(
    string RequestKind,
    string Fingerprint,
    long FencingToken,
    DateTimeOffset DeadlineUtc,
    PluginDeviceCommandIdempotencyClass IdempotencyClass,
    PluginDeviceCommandSafetyClass SafetyClass,
    string? RequestPayloadJson);

internal sealed record JournalCommandResponsePayload(
    PluginDeviceOperationOutcome Outcome,
    DateTimeOffset CompletedAtUtc,
    string? OutputPayload,
    string? FailureReason,
    PluginDeviceOperationCompletionState CompletionState =
        PluginDeviceOperationCompletionState.Known);

internal sealed record JournalSignalPayload(
    PluginDeviceValueType ValueType,
    string CanonicalValue,
    string? Unit,
    PluginDeviceSignalQuality Quality,
    string? QualityCode);

internal sealed class DeviceSessionJournalWriter : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStream _stream;
    private long _nextSequence;
    private string? _previousHash;
    private bool _disposed;

    private DeviceSessionJournalWriter(
        FileStream stream,
        long nextSequence,
        string? previousHash,
        IReadOnlyList<DeviceSessionJournalEntry> existingEntries)
    {
        _stream = stream;
        _nextSequence = nextSequence;
        _previousHash = previousHash;
        ExistingEntries = existingEntries;
    }

    public IReadOnlyList<DeviceSessionJournalEntry> ExistingEntries { get; }

    public static async ValueTask<DeviceSessionJournalWriter> OpenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Journal directory '{directory}' does not exist.");
        }

        IReadOnlyList<DeviceSessionJournalEntry> existing = [];
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            existing = await DeviceSessionJournal
                .ReadAndVerifyAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }

        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        return new DeviceSessionJournalWriter(
            stream,
            existing.Count + 1L,
            existing.Count == 0
                ? null
                : await ComputeHashAsync(existing[^1], cancellationToken)
                    .ConfigureAwait(false),
            existing);
    }

    public ValueTask AppendCommandRequestAsync(
        string deviceInstanceId,
        PluginDeviceCommandEnvelope command,
        string requestKind,
        string fingerprint,
        string? requestPayloadJson,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken) =>
        AppendAsync(
            DeviceSessionJournal.CommandRequestKind,
            deviceInstanceId,
            command.CommandId,
            null,
            null,
            null,
            null,
            DeviceSessionJournal.SerializePayload(new JournalCommandRequestPayload(
                requestKind,
                fingerprint,
                command.FencingToken,
                command.DeadlineUtc,
                command.IdempotencyClass,
                command.SafetyClass,
                requestPayloadJson)),
            recordedAtUtc,
            cancellationToken);

    public ValueTask AppendCommandResponseAsync(
        string deviceInstanceId,
        string commandId,
        PluginDeviceOperationResult result,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken) =>
        AppendAsync(
            DeviceSessionJournal.CommandResponseKind,
            deviceInstanceId,
            commandId,
            null,
            null,
            null,
            null,
            DeviceSessionJournal.SerializePayload(new JournalCommandResponsePayload(
                result.Outcome,
                result.CompletedAtUtc,
                result.OutputPayload,
                result.FailureReason,
                result.CompletionState)),
            recordedAtUtc,
            cancellationToken);

    public ValueTask AppendSignalAsync(
        string deviceInstanceId,
        PluginDeviceSignalSample sample,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken) =>
        AppendAsync(
            DeviceSessionJournal.SignalSampleKind,
            deviceInstanceId,
            null,
            sample.Sequence,
            sample.SignalId,
            sample.SourceTimestampUtc,
            sample.ObservedTimestampUtc,
            DeviceSessionJournal.SerializePayload(new JournalSignalPayload(
                sample.Value.Type,
                sample.Value.CanonicalValue,
                sample.Unit,
                sample.Quality,
                sample.QualityCode)),
            recordedAtUtc,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _stream.FlushAsync().ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask AppendAsync(
        string kind,
        string deviceInstanceId,
        string? correlationId,
        long? deviceSequence,
        string? signalId,
        DateTimeOffset? sourceTimestampUtc,
        DateTimeOffset? observedTimestampUtc,
        string? payloadJson,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = new DeviceSessionJournalEntry(
                DeviceSessionJournal.SchemaVersion,
                _nextSequence,
                kind,
                recordedAtUtc,
                deviceInstanceId,
                correlationId,
                deviceSequence,
                signalId,
                sourceTimestampUtc,
                observedTimestampUtc,
                payloadJson,
                _previousHash);
            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(
                entry,
                DeviceSessionJournal.JsonOptions);
            var hash = Convert.ToHexString(SHA256.HashData(canonicalBytes))
                .ToLowerInvariant();
            var lineBytes = JsonSerializer.SerializeToUtf8Bytes(
                new DeviceSessionJournalLine(entry, hash),
                DeviceSessionJournal.JsonOptions);
            cancellationToken.ThrowIfCancellationRequested();
            var terminatedLine = new byte[lineBytes.Length + 1];
            lineBytes.CopyTo(terminatedLine, 0);
            terminatedLine[^1] = (byte)'\n';
            await _stream.WriteAsync(terminatedLine, CancellationToken.None)
                .ConfigureAwait(false);
            await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            _nextSequence++;
            _previousHash = hash;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ValueTask<string> ComputeHashAsync(
        DeviceSessionJournalEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            entry,
            DeviceSessionJournal.JsonOptions);
        return ValueTask.FromResult(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
