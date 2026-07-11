using System.Text.Json;
using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Infrastructure.Packages;

internal static class StationPackageJson
{
    public static JsonSerializerOptions Options { get; } =
        StationPackageCanonicalization.CreateJsonOptions();
}
