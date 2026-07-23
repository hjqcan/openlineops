using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Plugins.Application.Extensions;
using OpenLineOps.Plugins.Infrastructure.Extensions;

namespace OpenLineOps.Plugins.Api.DependencyInjection;

public static class PluginsApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsPluginsApi(this IMvcBuilder mvcBuilder)
    {
        mvcBuilder.Services.TryAddSingleton<IApplicationExtensionPackageService,
            FileSystemApplicationExtensionPackageService>();
        return mvcBuilder.AddApplicationPart(typeof(PluginsApiMvcBuilderExtensions).Assembly);
    }
}
