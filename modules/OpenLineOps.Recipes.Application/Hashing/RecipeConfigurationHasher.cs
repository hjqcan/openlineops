using System.Security.Cryptography;
using System.Text.Json;
using OpenLineOps.Recipes.Domain.Deployments;
using OpenLineOps.Recipes.Domain.Recipes;

namespace OpenLineOps.Recipes.Application.Hashing;

public static class RecipeConfigurationHasher
{
    public static string Compute(
        string recipeId,
        string versionId,
        IEnumerable<RecipeParameterSnapshot> parameters)
    {
        return RecipeConfigurationIntegrity.Compute(recipeId, versionId, parameters);
    }

    public static string ComputeReadback(
        IEnumerable<RecipeReadbackParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var parameter in parameters.OrderBy(
                         static parameter => parameter.Key,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("key", parameter.Key);
                writer.WriteString("type", parameter.Type.ToString());
                if (parameter.Unit is null)
                {
                    writer.WriteNull("unit");
                }
                else
                {
                    writer.WriteString("unit", parameter.Unit);
                }

                writer.WriteString("value", parameter.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    internal static string ComputeCommand(params object?[] values)
    {
        var serialized = JsonSerializer.Serialize(values);
        return Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(serialized)));
    }
}
