using System.Text;
using System.Text.Json;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Application.Commands;

public static class RuntimeCommandEvidencePayload
{
    public const string PropertyName = "_openLineOpsEvidence";

    public static string Attach(
        string? resultPayload,
        ExecutionStatus executionStatus,
        ResultJudgement resultJudgement,
        IReadOnlyCollection<RuntimeCommandArtifactEvidence> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (!Enum.IsDefined(executionStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(executionStatus));
        }

        if (!Enum.IsDefined(resultJudgement))
        {
            throw new ArgumentOutOfRangeException(nameof(resultJudgement));
        }

        using var payloadDocument = ParseObject(resultPayload);
        if (payloadDocument.RootElement.TryGetProperty(PropertyName, out _))
        {
            throw new InvalidDataException(
                $"Command result payload cannot define reserved property '{PropertyName}'.");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in payloadDocument.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteStartObject(PropertyName);
            writer.WriteString("executionStatus", executionStatus.ToString());
            writer.WriteString("resultJudgement", resultJudgement.ToString());
            writer.WriteStartArray("artifacts");
            foreach (var artifact in artifacts.OrderBy(item => item.StorageKey, StringComparer.Ordinal))
            {
                ValidateArtifact(artifact);
                writer.WriteStartObject();
                writer.WriteString("name", artifact.Name);
                writer.WriteString("kind", artifact.Kind);
                writer.WriteString("storageKey", artifact.StorageKey);
                if (artifact.MediaType is not null)
                {
                    writer.WriteString("mediaType", artifact.MediaType);
                }

                writer.WriteNumber("sizeBytes", artifact.SizeBytes);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryRead(
        string? payload,
        out RuntimeCommandEvidence? evidence)
    {
        evidence = null;
        if (payload is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || HasDuplicateProperties(document.RootElement)
                || !document.RootElement.TryGetProperty(PropertyName, out var evidenceElement)
                || evidenceElement.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(
                    evidenceElement,
                    ["executionStatus", "resultJudgement", "artifacts"])
                || !TryReadRequiredString(evidenceElement, "executionStatus", out var executionStatus)
                || !TryReadRequiredString(evidenceElement, "resultJudgement", out var judgement)
                || !evidenceElement.TryGetProperty("artifacts", out var artifactsElement)
                || artifactsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var artifacts = new List<RuntimeCommandArtifactEvidence>();
            foreach (var artifactElement in artifactsElement.EnumerateArray())
            {
                if (!TryReadArtifact(artifactElement, out var artifact))
                {
                    return false;
                }

                artifacts.Add(artifact!);
            }

            if (artifacts.Select(item => item.StorageKey).Distinct(StringComparer.Ordinal).Count()
                != artifacts.Count)
            {
                return false;
            }

            if (!TryParseExact(executionStatus!, out ExecutionStatus parsedStatus)
                || !TryParseExact(judgement!, out ResultJudgement parsedJudgement))
            {
                return false;
            }

            evidence = new RuntimeCommandEvidence(parsedStatus, parsedJudgement, artifacts);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static RuntimeCommandEvidence? Read(string? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(PropertyName, out _))
            {
                return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return TryRead(payload, out var evidence)
            ? evidence
            : throw new InvalidDataException(
                $"Command payload contains invalid reserved evidence property '{PropertyName}'.");
    }

    private static JsonDocument ParseObject(string? payload)
    {
        if (payload is null)
        {
            return JsonDocument.Parse("{}");
        }

        var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || HasDuplicateProperties(document.RootElement))
        {
            document.Dispose();
            throw new InvalidDataException(
                "Command evidence can only be attached to one JSON object without duplicate properties.");
        }

        return document;
    }

    private static bool TryReadArtifact(
        JsonElement element,
        out RuntimeCommandArtifactEvidence? artifact)
    {
        artifact = null;
        if (element.ValueKind != JsonValueKind.Object
            || !HasOnlyProperties(
                element,
                ["name", "kind", "storageKey", "mediaType", "sizeBytes", "sha256"])
            || !TryReadRequiredString(element, "name", out var name)
            || !TryReadRequiredString(element, "kind", out var kind)
            || !TryReadRequiredString(element, "storageKey", out var storageKey)
            || !element.TryGetProperty("sizeBytes", out var sizeElement)
            || !sizeElement.TryGetInt64(out var sizeBytes)
            || !TryReadRequiredString(element, "sha256", out var sha256))
        {
            return false;
        }

        string? mediaType = null;
        if (element.TryGetProperty("mediaType", out var mediaTypeElement))
        {
            if (mediaTypeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            mediaType = mediaTypeElement.GetString();
        }

        var candidate = new RuntimeCommandArtifactEvidence(
            name!,
            kind!,
            storageKey!,
            mediaType,
            sizeBytes,
            sha256!);
        try
        {
            ValidateArtifact(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }

        artifact = candidate;
        return true;
    }

    private static bool TryReadRequiredString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return IsCanonical(value);
    }

    private static void ValidateArtifact(RuntimeCommandArtifactEvidence artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireCanonical(artifact.Name, nameof(artifact.Name));
        RequireToken(artifact.Kind, nameof(artifact.Kind));
        RequireCanonical(artifact.StorageKey, nameof(artifact.StorageKey));
        if (artifact.StorageKey.Contains('\\', StringComparison.Ordinal)
            || artifact.StorageKey.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException(
                "Artifact storage key must be one canonical relative '/' path.",
                nameof(artifact));
        }

        if (artifact.MediaType is not null)
        {
            RequireCanonical(artifact.MediaType, nameof(artifact.MediaType));
        }

        if (artifact.SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(artifact), "Artifact size cannot be negative.");
        }

        if (artifact.Sha256.Length != 64
            || artifact.Sha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("Artifact SHA-256 must be lowercase hexadecimal.", nameof(artifact));
        }
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasOnlyProperties(
        JsonElement element,
        IReadOnlyCollection<string> allowedProperties)
    {
        return element.EnumerateObject().All(property => allowedProperties.Contains(property.Name));
    }

    private static bool TryParseExact<TEnum>(string value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        return Enum.TryParse(value, ignoreCase: false, out parsed)
               && Enum.IsDefined(parsed)
               && string.Equals(parsed.ToString(), value, StringComparison.Ordinal);
    }

    private static void RequireToken(string value, string parameterName)
    {
        RequireCanonical(value, parameterName);
        if (value.Any(character => !char.IsAsciiLetter(character)))
        {
            throw new ArgumentException("Evidence token must contain only ASCII letters.", parameterName);
        }
    }

    private static void RequireCanonical(string value, string parameterName)
    {
        if (!IsCanonical(value))
        {
            throw new ArgumentException("Evidence value must be canonical text.", parameterName);
        }
    }

    private static bool IsCanonical(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !char.IsWhiteSpace(value[0])
               && !char.IsWhiteSpace(value[^1]);
    }
}

public sealed record RuntimeCommandEvidence(
    ExecutionStatus ExecutionStatus,
    ResultJudgement ResultJudgement,
    IReadOnlyCollection<RuntimeCommandArtifactEvidence> Artifacts);

public sealed record RuntimeCommandArtifactEvidence(
    string Name,
    string Kind,
    string StorageKey,
    string? MediaType,
    long SizeBytes,
    string Sha256);
