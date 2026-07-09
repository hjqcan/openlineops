using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Topology.Api.Controllers;

namespace OpenLineOps.Topology.Api.DependencyInjection;

public static class TopologyApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsTopologyApi(this IMvcBuilder mvcBuilder)
    {
        return mvcBuilder.AddApplicationPart(typeof(AutomationTopologiesController).Assembly);
    }
}
