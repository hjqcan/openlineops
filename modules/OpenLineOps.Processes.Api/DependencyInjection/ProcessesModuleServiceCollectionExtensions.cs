using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Processes.Api.Scripting;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.FlowIr;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Application.ProjectWorkspaces;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Processes.Application.Scripting;
using OpenLineOps.Processes.Infrastructure.Persistence;
using OpenLineOps.Processes.Infrastructure.Scripting;
using OpenLineOps.Processes.Infrastructure.Time;

namespace OpenLineOps.Processes.Api.DependencyInjection;

public static class ProcessesModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOpenLineOpsProcessesModule(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProcessBlocklyBlockCatalogSource, PluginCommandBlocklyBlockCatalogSource>());
        services.TryAddSingleton<IProcessScriptDefinitionValidator, PythonScriptDefinitionValidator>();
        services.TryAddSingleton<IProcessFlowIrCompiler, ProcessFlowIrCompiler>();
        services.TryAddSingleton<IFlowIrCanonicalSerializer, FlowIrCanonicalSerializer>();
        services.TryAddSingleton<
            IFlowIrExecutableRuntimeProcessMapper,
            FlowIrExecutableRuntimeProcessMapper>();
        services.TryAddSingleton<IProjectProcessDefinitionRepository, FileSystemProjectProcessDefinitionRepository>();
        services.TryAddSingleton<
            IProjectProcessBlocklyBlockDefinitionRepository,
            FileSystemProjectProcessBlocklyBlockDefinitionRepository>();
        services.AddScoped<IProjectProcessDefinitionService, ProjectProcessDefinitionService>();
        services.AddScoped<IProjectProcessBlocklyBlockCatalog, ProjectProcessBlocklyBlockCatalog>();

        return services;
    }
}
