using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Engineering.Api.Controllers;

namespace OpenLineOps.Engineering.Api.DependencyInjection;

public static class EngineeringApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsEngineeringApi(this IMvcBuilder mvcBuilder)
    {
        return mvcBuilder.AddApplicationPart(typeof(ProjectApplicationEngineeringConfigurationController).Assembly);
    }
}
