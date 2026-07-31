using System.Globalization;
using System.Text.Json;

namespace OpenLineOps.Runtime.Contracts;

public static class ProductionContextDocument
{
    public static JsonElement Write(
        IReadOnlyDictionary<string, ProductionContextValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                ValidateKey(key);
                ArgumentNullException.ThrowIfNull(value);
                writer.WritePropertyName(key);
                writer.WriteStartObject();
                writer.WriteString("kind", value.Kind.ToString());
                writer.WriteString("value", value.CanonicalValue);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    public static IReadOnlyDictionary<string, ProductionContextValue> Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Production Context must be one JSON object.");
        }

        var result = new Dictionary<string, ProductionContextValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            ValidateKey(property.Name);
            if (!result.TryAdd(property.Name, ReadValue(property.Name, property.Value)))
            {
                throw new InvalidDataException(
                    $"Production Context contains duplicate key '{property.Name}'.");
            }
        }

        if (result.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
        {
            throw new InvalidDataException(
                "Production Context keys cannot differ only by case.");
        }

        return result;
    }

    public static JsonElement WriteResolvedValues(
        IReadOnlyDictionary<string, ProductionContextValue> values)
    {
        var validated = Read(Write(values));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in validated.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                switch (value.Kind)
                {
                    case ProductionContextValueKind.Text:
                    case ProductionContextValueKind.DateTimeUtc:
                        writer.WriteString(key, value.CanonicalValue);
                        break;
                    case ProductionContextValueKind.Boolean:
                        writer.WriteBoolean(
                            key,
                            string.Equals(value.CanonicalValue, "true", StringComparison.Ordinal));
                        break;
                    case ProductionContextValueKind.WholeNumber:
                        writer.WriteNumber(
                            key,
                            long.Parse(
                                value.CanonicalValue,
                                NumberStyles.AllowLeadingSign,
                                CultureInfo.InvariantCulture));
                        break;
                    case ProductionContextValueKind.FixedPoint:
                        writer.WriteNumber(
                            key,
                            decimal.Parse(
                                value.CanonicalValue,
                                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture));
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unsupported Production Context value kind {value.Kind}.");
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static ProductionContextValue ReadValue(string key, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || element.EnumerateObject().Count() != 2
            || !element.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || !element.TryGetProperty("value", out var valueElement)
            || valueElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Production Context value '{key}' must contain only string kind and value fields.");
        }

        var token = kindElement.GetString();
        if (!Enum.TryParse<ProductionContextValueKind>(token, false, out var kind)
            || !Enum.IsDefined(kind)
            || !string.Equals(token, kind.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Production Context value '{key}' has invalid kind '{token}'.");
        }

        try
        {
            return new ProductionContextValue(kind, valueElement.GetString()!);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Production Context value '{key}' is not canonical for {kind}.",
                exception);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)
            || key.Length > 256
            || char.IsWhiteSpace(key[0])
            || char.IsWhiteSpace(key[^1])
            || key.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "Production Context keys must be canonical text of at most 256 characters.");
        }
    }
}
