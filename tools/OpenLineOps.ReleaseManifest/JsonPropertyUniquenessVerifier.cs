using System.Text.Json;

namespace OpenLineOps.ReleaseManifest;

public static class JsonPropertyUniquenessVerifier
{
    public static void VerifyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"JSON document '{path}' does not exist.", path);
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw new JsonException($"JSON document '{path}' must be UTF-8 without a byte-order mark.");
        }

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });
        var objectProperties = new Stack<HashSet<string>>();
        var foundToken = false;
        while (reader.Read())
        {
            foundToken = true;
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0)
                    {
                        throw new JsonException($"JSON document '{path}' has a property outside an object.");
                    }

                    var propertyName = reader.GetString()
                        ?? throw new JsonException($"JSON document '{path}' has a null property name.");
                    if (!objectProperties.Peek().Add(propertyName))
                    {
                        throw new JsonException(
                            $"JSON document '{path}' contains duplicate property '{propertyName}'.");
                    }

                    break;
                case JsonTokenType.EndObject:
                    if (objectProperties.Count == 0)
                    {
                        throw new JsonException($"JSON document '{path}' has an unmatched object terminator.");
                    }

                    objectProperties.Pop();
                    break;
            }
        }

        if (!foundToken || objectProperties.Count != 0)
        {
            throw new JsonException($"JSON document '{path}' is incomplete.");
        }
    }
}
