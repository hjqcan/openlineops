using System.Text.Json;
using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Application.Runs;

public static class ProductionContextOutputReader
{
    public static IReadOnlyDictionary<string, ProductionContextValue> Read(JsonElement payload) =>
        Read(payload.GetRawText());

    public static IReadOnlyDictionary<string, ProductionContextValue> Read(string? payload)
    {
        if (payload is null)
        {
            return new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || HasDuplicateProperties(document.RootElement))
            {
                throw new InvalidDataException(
                    "Production Context output must be one JSON object without duplicate properties.");
            }

            _ = RuntimeCommandEvidencePayload.Read(payload);
            var outputs = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(
                        property.Name,
                        RuntimeCommandEvidencePayload.PropertyName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!IsCanonical(property.Name)
                    || property.Value.ValueKind != JsonValueKind.Object
                    || property.Value.EnumerateObject().Count() != 2
                    || !property.Value.TryGetProperty("kind", out var kindElement)
                    || !property.Value.TryGetProperty("value", out var valueElement)
                    || kindElement.ValueKind != JsonValueKind.String
                    || valueElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException(
                        $"Production Context output '{property.Name}' must contain only string kind and value fields.");
                }

                var kindToken = kindElement.GetString();
                if (!Enum.TryParse<ProductionContextValueKind>(
                        kindToken,
                        ignoreCase: false,
                        out var kind)
                    || !Enum.IsDefined(kind)
                    || !string.Equals(kind.ToString(), kindToken, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Production Context output '{property.Name}' has invalid kind '{kindToken}'.");
                }

                try
                {
                    outputs.Add(
                        property.Name,
                        new ProductionContextValue(kind, valueElement.GetString()!));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"Production Context output '{property.Name}' has an invalid canonical value.",
                        exception);
                }
            }

            return outputs;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Production Context output is not valid JSON.", exception);
        }
    }

    public static IReadOnlyDictionary<string, ProductionContextValue> ReadMany(
        IEnumerable<string?> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var outputs = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            foreach (var (key, value) in Read(payload))
            {
                if (!outputs.TryAdd(key, value))
                {
                    throw new InvalidDataException(
                        $"Production Context output '{key}' is produced by multiple commands.");
                }
            }
        }

        return outputs;
    }

    public static IReadOnlyDictionary<string, ProductionContextValue> ReadExplicitMany(
        IEnumerable<string?> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var outputs = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            if (!TryReadExplicit(payload, out var explicitOutputs))
            {
                continue;
            }

            foreach (var (key, value) in explicitOutputs)
            {
                if (!outputs.TryAdd(key, value))
                {
                    throw new InvalidDataException(
                        $"Production Context output '{key}' is produced by multiple commands.");
                }
            }
        }

        return outputs;
    }

    public static bool TryReadExplicit(
        string? payload,
        out IReadOnlyDictionary<string, ProductionContextValue> outputs)
    {
        outputs = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var hasReservedEvidence = document.RootElement.TryGetProperty(
                RuntimeCommandEvidencePayload.PropertyName,
                out _);
            if (hasReservedEvidence)
            {
                _ = RuntimeCommandEvidencePayload.Read(payload);
            }

            var candidates = document.RootElement.EnumerateObject()
                .Where(property => !string.Equals(
                    property.Name,
                    RuntimeCommandEvidencePayload.PropertyName,
                    StringComparison.Ordinal))
                .ToArray();
            var looksTyped = candidates.Any(static property =>
                property.Value.ValueKind == JsonValueKind.Object
                && (property.Value.TryGetProperty("kind", out _)
                    || property.Value.TryGetProperty("value", out _)));
            if (!looksTyped)
            {
                return false;
            }

            if (HasDuplicateProperties(document.RootElement)
                || candidates.Any(static property => !IsExactTypedValue(property.Value)))
            {
                throw new InvalidDataException(
                    "Production Context output payload partially resembles the typed schema but is not exact.");
            }
        }

        outputs = Read(payload);
        return true;
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

    private static bool IsExactTypedValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == 2
        && value.TryGetProperty("kind", out var kind)
        && kind.ValueKind == JsonValueKind.String
        && value.TryGetProperty("value", out var canonicalValue)
        && canonicalValue.ValueKind == JsonValueKind.String;

    private static bool IsCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1]);
}
