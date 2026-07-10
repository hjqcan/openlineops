using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Production.Application.LineDefinitions;
using OpenLineOps.Production.Application.Persistence;
using OpenLineOps.Production.Infrastructure.Persistence;
using OpenLineOps.Production.Infrastructure.Time;

namespace OpenLineOps.Production.Api.DependencyInjection;

public static class ProductionModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsProductionModule(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<
            IProjectProductionLineDefinitionRepository,
            FileSystemProjectProductionLineDefinitionRepository>();
        services.AddScoped<
            IProjectProductionLineDefinitionService,
            ProjectProductionLineDefinitionService>();
        return services;
    }
}
