using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.Production.Api.Controllers;

namespace OpenLineOps.Production.Api.DependencyInjection;

public static class ProductionApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsProductionApi(this IMvcBuilder mvcBuilder)
    {
        ArgumentNullException.ThrowIfNull(mvcBuilder);

        return mvcBuilder.AddApplicationPart(typeof(ProductionLineDefinitionsController).Assembly);
    }
}
