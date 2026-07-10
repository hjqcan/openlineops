using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Plugin.Abstractions;

namespace OpenLineOps.Plugins.Infrastructure.Serialization;

internal static class PluginJsonContracts
{
    public static JsonSerializerOptions ManifestOptions { get; } = CreateManifestOptions();

    public static JsonSerializerOptions SignatureOptions { get; } = CreateStrictOptions();

    public static string RequireCanonicalEntryAssembly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || Path.IsPathRooted(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Any(char.IsControl)
            || value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                "Plugin entryAssembly must be a canonical forward-slash package-relative path.");
        }

        return value;
    }

    private static JsonSerializerOptions CreateManifestOptions()
    {
        var options = CreateStrictOptions();
        options.Converters.Add(new CanonicalPluginKindJsonConverter());
        return options;
    }

    private static JsonSerializerOptions CreateStrictOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
    }

    private sealed class CanonicalPluginKindJsonConverter : JsonConverter<PluginKind>
    {
        public override PluginKind Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(
                    $"Plugin kind must be one of the exact string tokens: {ExpectedTokens()}.");
            }

            var value = reader.GetString();
            foreach (var kind in Enum.GetValues<PluginKind>())
            {
                if (string.Equals(value, kind.ToString(), StringComparison.Ordinal))
                {
                    return kind;
                }
            }

            throw new JsonException(
                $"Unsupported plugin kind '{value}'. Expected exactly one of: {ExpectedTokens()}.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            PluginKind value,
            JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value))
            {
                throw new JsonException($"Unsupported plugin kind value '{value}'.");
            }

            writer.WriteStringValue(value.ToString());
        }

        private static string ExpectedTokens()
        {
            return string.Join(", ", Enum.GetNames<PluginKind>().Select(name => $"'{name}'"));
        }
    }
}
