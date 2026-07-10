using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Processes.Api.Controllers;

namespace OpenLineOps.Processes.Api.DependencyInjection;

public static class ProcessesApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsProcessesApi(this IMvcBuilder mvcBuilder)
    {
        return mvcBuilder.AddApplicationPart(typeof(ProjectApplicationProcessDefinitionsController).Assembly);
    }
}
