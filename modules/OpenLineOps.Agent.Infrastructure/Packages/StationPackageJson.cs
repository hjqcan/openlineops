using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLineOps.Agent.Infrastructure.Packages;

internal static class StationPackageJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false
    };
}
