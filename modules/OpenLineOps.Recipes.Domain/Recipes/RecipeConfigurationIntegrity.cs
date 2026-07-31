using System.Security.Cryptography;
using System.Text.Json;

namespace OpenLineOps.Recipes.Domain.Recipes;

public static class RecipeConfigurationIntegrity
{
    public static string Compute(
        string recipeId,
        string versionId,
        IEnumerable<RecipeParameterSnapshot> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("recipeId", recipeId);
            writer.WriteString("versionId", versionId);
            writer.WritePropertyName("parameters");
            writer.WriteStartArray();
            foreach (var parameter in parameters.OrderBy(
                         static parameter => parameter.Key,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("key", parameter.Key);
                writer.WriteString("type", parameter.Type.ToString());
                writer.WriteBoolean("required", parameter.Required);
                if (parameter.Unit is null)
                {
                    writer.WriteNull("unit");
                }
                else
                {
                    writer.WriteString("unit", parameter.Unit);
                }

                writer.WriteString("value", parameter.CanonicalValue);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
