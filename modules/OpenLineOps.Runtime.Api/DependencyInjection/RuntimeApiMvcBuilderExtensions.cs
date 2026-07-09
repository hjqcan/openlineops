using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenLineOps.Runtime.Api.Controllers;
using OpenLineOps.Runtime.Api.Hubs;
using OpenLineOps.Runtime.Application.Events;

namespace OpenLineOps.Runtime.Api.DependencyInjection;

public static class RuntimeApiMvcBuilderExtensions
{
    public static IMvcBuilder AddOpenLineOpsRuntimeApi(this IMvcBuilder mvcBuilder)
    {
        mvcBuilder.Services.AddSignalR();
        mvcBuilder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRuntimeDomainEventSubscriber, RuntimeProgressHubDomainEventSubscriber>());

        return mvcBuilder.AddApplicationPart(typeof(RuntimeSessionsController).Assembly);
    }
}
