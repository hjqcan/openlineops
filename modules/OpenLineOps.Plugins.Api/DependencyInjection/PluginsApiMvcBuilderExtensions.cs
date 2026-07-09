using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Plugins.Api.Controllers;

namespace OpenLineOps.Plugins.Api.DependencyInjection;

public static class PluginsApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsPluginsApi(this IMvcBuilder mvcBuilder)
    {
        return mvcBuilder.AddApplicationPart(typeof(PluginManagementController).Assembly);
    }
}
