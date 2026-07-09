using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Projects.Api.Controllers;

namespace OpenLineOps.Projects.Api.DependencyInjection;

public static class ProjectsApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsProjectsApi(this IMvcBuilder mvcBuilder)
    {
        return mvcBuilder.AddApplicationPart(typeof(AutomationProjectsController).Assembly);
    }
}
