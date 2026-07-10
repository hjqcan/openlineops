using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal static class RuntimePersistenceJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
    }
}
