using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using OpenLineOps.Runtime.Api.Hubs;

namespace OpenLineOps.Runtime.Api.DependencyInjection;

public static class RuntimeEndpointRouteBuilderExtensions
{
    public const string RuntimeProgressHubPath = "/hubs/runtime-progress";

    public static IEndpointRouteBuilder MapOpenLineOpsRuntimeRealtime(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<RuntimeProgressHub>(RuntimeProgressHubPath);

        return endpoints;
    }
}
