using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenLineOps.SampleInspection.Application.Plans;
using OpenLineOps.SampleInspection.Infrastructure.Persistence;

namespace OpenLineOps.SampleInspection.Infrastructure.DependencyInjection;

public static class SampleInspectionPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddSampleInspectionPersistence(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureDbContext)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureDbContext);

        services.AddDbContext<InspectionDbContext>(configureDbContext);
        services.AddScoped<IInspectionPlanRepository, EfInspectionPlanRepository>();

        return services;
    }
}
