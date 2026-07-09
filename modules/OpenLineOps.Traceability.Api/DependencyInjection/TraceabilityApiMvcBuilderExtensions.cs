using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Traceability.Api.Controllers;

namespace OpenLineOps.Traceability.Api.DependencyInjection;

public static class TraceabilityApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsTraceabilityApi(this IMvcBuilder mvcBuilder)
    {
        return mvcBuilder.AddApplicationPart(typeof(TraceRecordsController).Assembly);
    }
}
