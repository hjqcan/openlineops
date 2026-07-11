using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenLineOps.Api.Abstractions;

public static class EditorDocumentConcurrency
{
    public const string IfMatchHeaderName = "If-Match";
    public const string ConflictResolutionHeaderName = "X-OpenLineOps-Conflict-Resolution";
    public const string ExplicitOverwriteToken = "overwrite";

    private static readonly ConcurrentDictionary<string, DocumentGate> Gates =
        new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions RevisionSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string ComputeRevision<T>(T resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(resource, RevisionSerializerOptions);
        return Convert.ToHexStringLower(SHA256.HashData(canonicalBytes));
    }

    public static async ValueTask<IAsyncDisposable> AcquireAsync(
        string documentKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKey);
        while (true)
        {
            var gate = Gates.GetOrAdd(documentKey, static _ => new DocumentGate());
            if (!gate.TryAddReference())
            {
                continue;
            }

            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new GateLease(documentKey, gate);
            }
            catch
            {
                RetireReference(documentKey, gate);
                throw;
            }
        }
    }

    public static EditorDocumentPrecondition Evaluate(
        string? ifMatch,
        string? conflictResolution,
        string currentRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRevision);
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return EditorDocumentPrecondition.Missing;
        }

        if (string.Equals(ifMatch.Trim(), "*", StringComparison.Ordinal))
        {
            return string.Equals(
                conflictResolution,
                ExplicitOverwriteToken,
                StringComparison.Ordinal)
                ? EditorDocumentPrecondition.Satisfied
                : EditorDocumentPrecondition.ForceNotExplicit;
        }

        var normalized = ifMatch.Trim();
        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
        {
            normalized = normalized[1..^1];
        }

        return string.Equals(normalized, currentRevision, StringComparison.Ordinal)
            ? EditorDocumentPrecondition.Satisfied
            : EditorDocumentPrecondition.Stale;
    }

    public static string ToEntityTag(string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        return $"\"{revision}\"";
    }

    private static void RetireReference(string documentKey, DocumentGate gate)
    {
        if (!gate.ReleaseReference())
        {
            return;
        }

        _ = ((ICollection<KeyValuePair<string, DocumentGate>>)Gates).Remove(
            new KeyValuePair<string, DocumentGate>(documentKey, gate));
        gate.Semaphore.Dispose();
    }

    private sealed class DocumentGate
    {
        private readonly object _sync = new();
        private int _referenceCount;
        private bool _retired;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddReference()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return false;
                }

                _referenceCount++;
                return true;
            }
        }

        public bool ReleaseReference()
        {
            lock (_sync)
            {
                if (_referenceCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Editor document gate reference count is inconsistent.");
                }

                _referenceCount--;
                if (_referenceCount != 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }

    private sealed class GateLease : IAsyncDisposable
    {
        private readonly string _documentKey;
        private DocumentGate? _gate;

        public GateLease(string documentKey, DocumentGate gate)
        {
            _documentKey = documentKey;
            _gate = gate;
        }

        public ValueTask DisposeAsync()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is not null)
            {
                gate.Semaphore.Release();
                RetireReference(_documentKey, gate);
            }

            return ValueTask.CompletedTask;
        }
    }
}

public enum EditorDocumentPrecondition
{
    Satisfied = 1,
    Missing = 2,
    Stale = 3,
    ForceNotExplicit = 4
}
